using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Monday.Domain.CodeGraph;

namespace Monday.Indexer;

/// <summary>The file the indexer writes and CI uploads: the same shape Monday's ingest endpoint takes.</summary>
internal sealed record GraphFile(string Commit, IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges, IReadOnlyList<SecurityFinding> Findings);

/// <summary>
/// Builds the code graph of a C# repo with Roslyn: modules (projects), features (top folders),
/// types, methods, minimal-API endpoints with their guards, and the edges between them.
/// All files go into one compilation so calls across projects resolve.
/// </summary>
internal sealed partial class RepoIndexer(string root, string appName)
{
    private const string AppId = "app";

    private static readonly string[] SkippedDirs = ["bin", "obj", "node_modules", ".git", ".monday"];

    private static readonly HashSet<string> MapVerbs = new(StringComparer.Ordinal) { "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch" };

    /// <summary>Endpoint and group modifiers that count as security guards.</summary>
    private static readonly HashSet<string> GuardMethods = new(StringComparer.Ordinal)
    {
        "RequireAuthorization", "AllowAnonymous", "RequireRateLimiting", "DisableRateLimiting", "RequireCors",
        "DisableAntiforgery", "RequireReporterKey", "RequireHost", "AddEndpointFilter",
    };

    /// <summary>Middleware calls on the app that form the request pipeline, in order.</summary>
    private static readonly HashSet<string> PipelineMethods = new(StringComparer.Ordinal)
    {
        "UseExceptionHandler", "UseHsts", "UseHttpsRedirection", "UseCors", "UseAuthentication", "UseAuthorization",
        "UseRateLimiter", "UseAntiforgery", "UseStaticFiles", "UseRouting", "UseOutputCache", "UseResponseCompression",
    };

    private readonly Dictionary<string, CodeNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<CodeEdge> _edges = [];

    public GraphFile Index(string commit)
    {
        Add(new CodeNode { Id = AppId, Kind = NodeKind.App, Name = appName });

        var modules = FindModules();
        var trees = new List<(SyntaxTree Tree, Module Module)>();
        foreach (var module in modules)
        {
            Add(new CodeNode { Id = module.Id, Kind = NodeKind.Module, Name = module.Name, ParentId = AppId, File = Rel(module.ProjectFile) });
            foreach (var file in SourceFiles(module.Dir))
            {
                trees.Add((CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file), module));
            }
        }

        var compilation = CSharpCompilation.Create(
            "monday-index",
            trees.Select(t => t.Tree).Append(ImplicitUsings()),
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        // Pass 1: nodes. Pass 2: edges (needs every node to exist first).
        foreach (var (tree, module) in trees)
        {
            CollectNodes(tree, compilation.GetSemanticModel(tree), module);
        }

        foreach (var (tree, module) in trees)
        {
            CollectEdges(tree, compilation.GetSemanticModel(tree), module);
        }

        var nodes = _nodes.Values.OrderBy(n => n.Kind).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        var edges = _edges.Where(e => e.From != e.To).OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
        var findings = ScanEndpointsAndPipeline();
        foreach (var (tree, module) in trees)
        {
            findings.AddRange(ScanCode(tree, compilation.GetSemanticModel(tree), module));
        }

        return new GraphFile(commit, nodes, edges, findings.OrderByDescending(f => f.Severity).ThenBy(f => f.NodeId, StringComparer.Ordinal).ToList());
    }

    // ---------- modules and files ----------

    private sealed record Module(string Id, string Name, string Dir, string ProjectFile, bool IsTest);

    private List<Module> FindModules() =>
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsSkipped(f))
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return new Module($"module:{name}", name, Path.GetDirectoryName(f)!, f, name.EndsWith(".Tests", StringComparison.Ordinal));
            })
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> SourceFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !IsSkipped(f));

    private static bool IsSkipped(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(part => SkippedDirs.Contains(part, StringComparer.OrdinalIgnoreCase));

    private string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>The top folder of a file inside its project becomes a feature ("Projects", "Endpoints").</summary>
    private string ParentFor(string file, Module module)
    {
        var relative = Path.GetRelativePath(module.Dir, file).Split(Path.DirectorySeparatorChar);
        if (relative.Length < 2)
        {
            return module.Id;
        }

        var featureId = $"feature:{module.Name}/{relative[0]}";
        if (!_nodes.ContainsKey(featureId))
        {
            Add(new CodeNode { Id = featureId, Kind = NodeKind.Feature, Name = relative[0], ParentId = module.Id, File = Rel(Path.Combine(module.Dir, relative[0])) });
        }

        return featureId;
    }

    // ---------- pass 1: nodes ----------

    private void CollectNodes(SyntaxTree tree, SemanticModel model, Module module)
    {
        var rootNode = tree.GetRoot();
        var file = tree.FilePath;

        foreach (var type in rootNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(type) is not INamedTypeSymbol symbol || IdOf(symbol) is not { } id)
            {
                continue;
            }

            var parent = symbol.ContainingType is { } outer && IdOf(outer) is { } outerId ? outerId : ParentFor(file, module);
            Add(Located(new CodeNode { Id = id, Kind = NodeKind.Type, Name = symbol.Name, ParentId = parent }, type, file, withCode: false));
        }

        foreach (var method in rootNode.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(method) is not IMethodSymbol symbol || IdOf(symbol) is not { } id || IdOf(symbol.ContainingType) is not { } typeId)
            {
                continue;
            }

            var isTest = module.IsTest && symbol.DeclaredAccessibility == Accessibility.Public && symbol.ContainingType.Name.EndsWith("Tests", StringComparison.Ordinal);
            Add(Located(new CodeNode { Id = id, Kind = isTest ? NodeKind.Test : NodeKind.Method, Name = symbol.Name, ParentId = typeId }, method, file, withCode: true));
        }

        // Top-level statements (Program.cs) become one "startup" node per module, with the request pipeline as its guards.
        var globals = rootNode.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count > 0)
        {
            var pipeline = globals.SelectMany(g => g.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Select(MethodName).Where(n => n is not null && PipelineMethods.Contains(n)).Select(n => n!).ToList();
            var span = globals[0].GetLocation().GetLineSpan();
            var end = globals[^1].GetLocation().GetLineSpan();
            var code = string.Join(Environment.NewLine, globals.Select(g => g.ToString()));
            Add(new CodeNode
            {
                Id = StartupId(module), Kind = NodeKind.Method, Name = "Startup", ParentId = module.Id, File = Rel(file),
                StartLine = span.StartLinePosition.Line + 1, EndLine = end.EndLinePosition.Line + 1,
                Code = code, CodeHash = CodeNode.HashCode(code), Guards = pipeline,
            });
        }

        foreach (var invocation in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (Endpoint(invocation, model, module, file) is { } endpoint)
            {
                Add(endpoint);
            }
        }
    }

    private CodeNode? Endpoint(InvocationExpressionSyntax invocation, SemanticModel model, Module module, string file)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access || !MapVerbs.Contains(access.Name.Identifier.Text))
        {
            return null;
        }

        if (invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax { Token.Value: string template })
        {
            return null;
        }

        var (prefix, groupGuards) = GroupOf(access.Expression, model);
        var verb = access.Name.Identifier.Text["Map".Length..].ToUpperInvariant();
        var route = $"{verb} {JoinRoute(prefix, template)}";
        var guards = groupGuards.Concat(ChainedGuards(invocation, model)).Distinct().ToList();
        var container = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var parent = container is not null && model.GetDeclaredSymbol(container) is IMethodSymbol m && IdOf(m.ContainingType) is { } typeId
            ? typeId
            : ParentFor(file, module);
        var span = invocation.GetLocation().GetLineSpan();
        return new CodeNode
        {
            Id = EndpointId(route), Kind = NodeKind.Endpoint, Name = route, ParentId = parent, File = Rel(file),
            StartLine = span.StartLinePosition.Line + 1, EndLine = span.EndLinePosition.Line + 1, Route = route, Guards = guards,
        };
    }

    // ---------- pass 2: edges ----------

    private void CollectEdges(SyntaxTree tree, SemanticModel model, Module module)
    {
        var rootNode = tree.GetRoot();

        foreach (var method in rootNode.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(method) is IMethodSymbol symbol && IdOf(symbol) is { } from && _nodes.TryGetValue(from, out var node))
            {
                LinkCalls(from, method, model, node.Kind == NodeKind.Test);
            }
        }

        var globals = rootNode.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        foreach (var global in globals)
        {
            LinkCalls(StartupId(module), global, model, isTest: false);
        }

        foreach (var type in rootNode.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(type) is INamedTypeSymbol symbol && IdOf(symbol) is { } typeId && _nodes.ContainsKey(typeId))
            {
                LinkTypeDependencies(typeId, symbol);
            }
        }

        foreach (var invocation in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            LinkEndpointHandler(invocation, model);
        }
    }

    /// <summary>Calls from a method; from a test, calls into product code are "covers" edges.</summary>
    private void LinkCalls(string from, SyntaxNode body, SemanticModel model, bool isTest)
    {
        foreach (var call in body.DescendantNodes().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
        {
            if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol target || TargetId(target) is not { } to)
            {
                continue;
            }

            var kind = isTest && _nodes[to].Kind != NodeKind.Test ? EdgeKind.Covers : EdgeKind.Calls;
            _edges.Add(new CodeEdge(from, to, kind));
        }
    }

    private void LinkTypeDependencies(string typeId, INamedTypeSymbol symbol)
    {
        foreach (var iface in symbol.AllInterfaces)
        {
            if (IdOf(iface) is { } ifaceId && _nodes.ContainsKey(ifaceId))
            {
                _edges.Add(new CodeEdge(typeId, ifaceId, EdgeKind.Implements));
            }
        }

        var dependencies = symbol.Constructors.SelectMany(c => c.Parameters).Select(p => p.Type)
            .Concat(symbol.GetMembers().OfType<IFieldSymbol>().Select(f => f.Type));
        foreach (var dependency in dependencies)
        {
            var named = dependency as INamedTypeSymbol;
            if (named is not null && IdOf(named.OriginalDefinition) is { } depId && depId != typeId && _nodes.ContainsKey(depId))
            {
                _edges.Add(new CodeEdge(typeId, depId, EdgeKind.DependsOn));
            }
        }
    }

    /// <summary>Endpoint → its handler: a method group becomes "handled by"; a lambda's calls are the endpoint's own calls.</summary>
    private void LinkEndpointHandler(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access || !MapVerbs.Contains(access.Name.Identifier.Text)
            || invocation.ArgumentList.Arguments.Count < 2
            || invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax { Token.Value: string template })
        {
            return;
        }

        var (prefix, _) = GroupOf(access.Expression, model);
        var endpointId = EndpointId($"{access.Name.Identifier.Text["Map".Length..].ToUpperInvariant()} {JoinRoute(prefix, template)}");
        // Guards written in this repo (like RequireReporterKey) run on every request: link them so the path shows them.
        foreach (var guardCall in GuardCalls(access.Expression, invocation, model))
        {
            if (model.GetSymbolInfo(guardCall).Symbol is IMethodSymbol guard && TargetId(guard) is { } guardId)
            {
                _edges.Add(new CodeEdge(endpointId, guardId, EdgeKind.Calls));
            }
        }

        var handler = invocation.ArgumentList.Arguments[1].Expression;
        if (handler is LambdaExpressionSyntax lambda)
        {
            LinkCalls(endpointId, lambda, model, isTest: false);
            return;
        }

        var symbolInfo = model.GetSymbolInfo(handler);
        var target = symbolInfo.Symbol as IMethodSymbol ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (target is not null && TargetId(target) is { } to)
        {
            _edges.Add(new CodeEdge(endpointId, to, EdgeKind.HandledBy));
        }
    }

    // ---------- route groups and guards ----------

    /// <summary>
    /// Follows the receiver of a MapX call back to its MapGroup(...) chain to find the route prefix
    /// and the guards applied to the whole group.
    /// </summary>
    private static (string Prefix, List<string> Guards) GroupOf(ExpressionSyntax receiver, SemanticModel model)
    {
        var chain = receiver;
        if (receiver is IdentifierNameSyntax identifier
            && model.GetSymbolInfo(identifier).Symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } init })
        {
            chain = init;
        }

        var prefix = string.Empty;
        var guards = new List<string>();
        foreach (var call in chain.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var name = MethodName(call);
            if (name == "MapGroup" && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.Value: string groupPrefix })
            {
                prefix = groupPrefix;
            }
            else if (name is not null && GuardMethods.Contains(name))
            {
                guards.Add(GuardLabel(call, name, model));
            }
        }

        guards.Reverse();
        return (prefix, guards);
    }

    private static IEnumerable<InvocationExpressionSyntax> GuardCalls(ExpressionSyntax receiver, InvocationExpressionSyntax mapCall, SemanticModel model)
    {
        var chain = receiver;
        if (receiver is IdentifierNameSyntax identifier
            && model.GetSymbolInfo(identifier).Symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } init })
        {
            chain = init;
        }

        var calls = chain.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToList();
        SyntaxNode current = mapCall;
        while (current.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } access && access.Expression == current)
        {
            calls.Add(outer);
            current = outer;
        }

        return calls.Where(c => MethodName(c) is { } n && GuardMethods.Contains(n));
    }

    /// <summary>Guards chained directly onto one endpoint: app.MapGet(...).AllowAnonymous().</summary>
    private static IEnumerable<string> ChainedGuards(InvocationExpressionSyntax mapCall, SemanticModel model)
    {
        SyntaxNode current = mapCall;
        while (current.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } access && access.Expression == current)
        {
            var name = access.Name.Identifier.Text;
            if (GuardMethods.Contains(name))
            {
                yield return GuardLabel(outer, name, model);
            }

            current = outer;
        }
    }

    /// <summary>"RequireRateLimiting:ingest": the guard plus its policy, resolving constants to their value.</summary>
    private static string GuardLabel(InvocationExpressionSyntax call, string name, SemanticModel model)
    {
        var argument = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (argument is null)
        {
            return name;
        }

        return model.GetConstantValue(argument) is { HasValue: true, Value: string value }
            ? $"{name}:{value}"
            : argument is MemberAccessExpressionSyntax m ? $"{name}:{m.Name.Identifier.Text}" : name;
    }

    private static string? MethodName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };

    private static string JoinRoute(string prefix, string template)
    {
        var joined = $"/{prefix.Trim('/')}/{template.Trim('/')}".Replace("//", "/", StringComparison.Ordinal);
        return joined.Length > 1 ? joined.TrimEnd('/') : joined;
    }

    // ---------- ids and helpers ----------

    private static string EndpointId(string route) => $"endpoint:{route}";

    private static string StartupId(Module module) => $"startup:{module.Name}";

    private static string? IdOf(ISymbol? symbol) =>
        symbol is null || symbol.Locations.All(l => !l.IsInSource) ? null : symbol.OriginalDefinition.GetDocumentationCommentId();

    /// <summary>The graph node a call lands on: extension and generic methods map to their declaration.</summary>
    private string? TargetId(IMethodSymbol target)
    {
        var declared = target.ReducedFrom ?? target;
        return IdOf(declared) is { } id && _nodes.ContainsKey(id) ? id : null;
    }

    private CodeNode Located(CodeNode node, SyntaxNode syntax, string file, bool withCode)
    {
        var span = syntax.GetLocation().GetLineSpan();
        var code = withCode ? syntax.ToString() : null;
        return node with
        {
            File = Rel(file),
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Code = code,
            CodeHash = code is null ? null : CodeNode.HashCode(code),
        };
    }

    private void Add(CodeNode node) => _nodes.TryAdd(node.Id, node);

    /// <summary>The implicit usings SDK-style projects get, so files compile the way they do in a real build.</summary>
    private static SyntaxTree ImplicitUsings() => CSharpSyntaxTree.ParseText("""
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Net.Http.Json;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """, path: "monday-implicit-usings.g.cs");

    /// <summary>Framework assemblies (.NET and ASP.NET Core) from the runtime running the indexer.</summary>
    private static IEnumerable<MetadataReference> References()
    {
        var netcore = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var version = new DirectoryInfo(netcore).Name;
        var shared = Directory.GetParent(netcore)!.Parent!.FullName;
        var aspnet = Path.Combine(shared, "Microsoft.AspNetCore.App", version);
        if (!Directory.Exists(aspnet))
        {
            aspnet = Directory.EnumerateDirectories(Path.Combine(shared, "Microsoft.AspNetCore.App")).OrderDescending().FirstOrDefault() ?? string.Empty;
        }

        return new[] { netcore, aspnet }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll"))
            .Where(IsManagedAssembly)
            .GroupBy(Path.GetFileName)
            .Select(g => MetadataReference.CreateFromFile(g.First()));
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}

internal static class Git
{
    public static string HeadCommit(string root)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", ["-C", root, "rev-parse", "--short=12", "HEAD"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var sha = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 && sha.Length > 0 ? sha : "uncommitted";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "uncommitted";
        }
    }
}
