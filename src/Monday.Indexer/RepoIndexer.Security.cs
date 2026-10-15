using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Monday.Domain.CodeGraph;

namespace Monday.Indexer;

/// <summary>
/// Static threat rules: places in the code where an attack could come from. Each finding names the
/// node it lives on, why it matters and how to fix it. Rules are deliberately conservative: a finding
/// is a prompt to look, and every rule says what would clear it.
/// </summary>
internal sealed partial class RepoIndexer
{
    private static readonly HashSet<string> WriteVerbs = new(StringComparer.Ordinal) { "POST", "PUT", "PATCH", "DELETE" };

    private static readonly HashSet<string> RawSqlMethods = new(StringComparer.Ordinal)
    {
        "FromSqlRaw", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "SqlQueryRaw",
    };

    private static readonly HashSet<string> SqlCommandTypes = new(StringComparer.Ordinal)
    {
        "SqlCommand", "NpgsqlCommand", "MySqlCommand", "SqliteCommand", "OracleCommand",
    };

    [GeneratedRegex("(password|passwd|pwd|secret|apikey|api_key|accesskey|access_key|token|connectionstring|privatekey|private_key|clientsecret)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"(-----BEGIN [A-Z ]*PRIVATE KEY-----|AKIA[0-9A-Z]{16}|\bsk-[A-Za-z0-9_\-]{20,}|ghp_[A-Za-z0-9]{30,}|(Password|Pwd)\s*=\s*[^;{\s]{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValue();

    [GeneratedRegex("(log-?in|sign-?in|token|auth|password|otp|verify)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveRoute();

    private static bool IsAuthGuard(string guard) =>
        guard.StartsWith("RequireAuthorization", StringComparison.Ordinal) || guard.StartsWith("RequireReporterKey", StringComparison.Ordinal);

    // ---------- endpoints and the request pipeline ----------

    private List<SecurityFinding> ScanEndpointsAndPipeline()
    {
        var findings = new List<SecurityFinding>();
        var endpoints = _nodes.Values.Where(n => n.Kind == NodeKind.Endpoint).ToList();

        foreach (var ep in endpoints)
        {
            var verb = ep.Route!.Split(' ')[0];
            var anonymous = ep.Guards.Contains("AllowAnonymous");
            var authed = !anonymous && ep.Guards.Any(IsAuthGuard);
            var limited = ep.Guards.Any(g => g.StartsWith("RequireRateLimiting", StringComparison.Ordinal));

            // A sign-in route has to be public; an explicit AllowAnonymous is a choice to review, not an oversight.
            if (!authed && WriteVerbs.Contains(verb) && !(anonymous && SensitiveRoute().IsMatch(ep.Route)))
            {
                findings.Add(Finding(ep, "write-without-auth", anonymous ? Severity.Medium : Severity.High,
                    anonymous ? $"{ep.Route} changes data and is marked public" : $"{ep.Route} changes data without sign-in",
                    "Anyone who can reach the API can call it. Writes are the first thing an attacker tries.",
                    anonymous ? "Confirm it must be public; otherwise remove .AllowAnonymous() and require sign-in." : "Add .RequireAuthorization() (or the project's key guard) to the endpoint or its group."));
            }

            if (!authed && !limited)
            {
                findings.Add(Finding(ep, "public-without-rate-limit", WriteVerbs.Contains(verb) ? Severity.Medium : Severity.Low,
                    $"{ep.Route} is public and has no rate limit",
                    "A public route without a limit can be flooded to slow the app down or to run up hosting costs.",
                    "Add .RequireRateLimiting(\"<policy>\") with a policy partitioned by client."));
            }

            if (SensitiveRoute().IsMatch(ep.Route) && !limited)
            {
                findings.Add(Finding(ep, "credential-route-without-rate-limit", Severity.Medium,
                    $"{ep.Route} looks like a sign-in or token route but has no rate limit",
                    "Without a limit, passwords and codes can be guessed by brute force.",
                    "Rate-limit by user and by IP, and lock out after repeated failures."));
            }

            if (!authed && ep.Guards.Contains("DisableAntiforgery"))
            {
                findings.Add(Finding(ep, "antiforgery-off-without-auth", Severity.Medium,
                    $"{ep.Route} turns off antiforgery without another guard",
                    "Browser form posts from other sites can reach this route on a signed-in user's behalf.",
                    "Keep antiforgery on, or require a key or token that browsers don't send automatically."));
            }
        }

        foreach (var startup in _nodes.Values.Where(n => n.Id.StartsWith("startup:", StringComparison.Ordinal)))
        {
            var moduleEndpoints = endpoints.Where(e => ModuleOf(e) == startup.ParentId).ToList();
            if (moduleEndpoints.Count == 0)
            {
                continue;
            }

            var pipeline = startup.Guards;
            if (!pipeline.Contains("UseHttpsRedirection"))
            {
                findings.Add(Finding(startup, "no-https-redirect", Severity.High, "Requests over plain HTTP are not redirected to HTTPS",
                    "Keys, cookies and data can travel unencrypted and be read on the way.", "Add app.UseHttpsRedirection() early in Program.cs."));
            }

            if (!pipeline.Contains("UseHsts"))
            {
                findings.Add(Finding(startup, "no-hsts", Severity.Medium, "Browsers aren't told to insist on HTTPS (no HSTS)",
                    "A first visit over HTTP can be intercepted and downgraded.", "Add app.UseHsts() outside Development."));
            }

            if (!pipeline.Contains("UseExceptionHandler"))
            {
                findings.Add(Finding(startup, "no-exception-handler", Severity.Low, "No central exception handler",
                    "Unhandled errors can leak stack traces and internal names to callers.", "Add app.UseExceptionHandler() with problem details."));
            }

            var authIndex = pipeline.ToList().IndexOf("UseAuthentication");
            var authzIndex = pipeline.ToList().IndexOf("UseAuthorization");
            if (authIndex >= 0 && authzIndex >= 0 && authzIndex < authIndex)
            {
                findings.Add(Finding(startup, "authorization-before-authentication", Severity.High, "Authorization runs before authentication",
                    "Policies are checked before the user is known, so they can fail open or always deny.", "Call app.UseAuthentication() before app.UseAuthorization()."));
            }

            if (moduleEndpoints.Any(e => e.Guards.Any(g => g.StartsWith("RequireRateLimiting", StringComparison.Ordinal))) && !pipeline.Contains("UseRateLimiter"))
            {
                findings.Add(Finding(startup, "rate-limiter-not-in-pipeline", Severity.High, "Endpoints ask for rate limits but the limiter isn't in the pipeline",
                    "The limits are silently not applied.", "Add app.UseRateLimiter() after routing and authentication."));
            }
        }

        return findings;
    }

    private string? ModuleOf(CodeNode node)
    {
        var current = node;
        while (current is not null && current.Kind != NodeKind.Module)
        {
            current = current.ParentId is { } p && _nodes.TryGetValue(p, out var parent) ? parent : null;
        }

        return current?.Id;
    }

    // ---------- code-level rules ----------

    private IEnumerable<SecurityFinding> ScanCode(SyntaxTree tree, SemanticModel model, Module module)
    {
        var rootNode = tree.GetRoot();
        var file = tree.FilePath;

        foreach (var invocation in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = MethodName(invocation);
            if (name is not null && RawSqlMethods.Contains(name) && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } sql && IsBuiltString(sql))
            {
                yield return CodeFinding(invocation, model, module, file, "sql-built-from-strings", Severity.High,
                    $"{name} runs SQL built from strings",
                    "If any part comes from a request, an attacker can change the query (SQL injection).",
                    "Use FromSql/ExecuteSql with an interpolated FormattableString, or parameters.");
            }

            if (invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "MD5" or "SHA1" } weak })
            {
                yield return CodeFinding(invocation, model, module, file, "weak-hash", Severity.Medium,
                    $"{weak.Identifier.Text} is used",
                    $"{weak.Identifier.Text} is broken for security uses: collisions can be forged.",
                    "Use SHA256 or better; for passwords use a password hasher (PBKDF2, bcrypt, Argon2).");
            }

            if (name == "AllowAnyOrigin")
            {
                var chain = invocation.Ancestors().OfType<InvocationExpressionSyntax>().LastOrDefault() ?? invocation;
                var withCredentials = chain.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) == "AllowCredentials");
                yield return CodeFinding(invocation, model, module, file, "cors-any-origin", withCredentials ? Severity.High : Severity.Medium,
                    withCredentials ? "CORS allows any site, with credentials" : "CORS allows any site",
                    "Any website can call this API from a visitor's browser" + (withCredentials ? ", carrying their sign-in." : "."),
                    "List the exact origins that need access with WithOrigins(...).");
            }
        }

        foreach (var creation in rootNode.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type switch
            {
                IdentifierNameSyntax i => i.Identifier.Text,
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                _ => null,
            };
            if (typeName is not null && SqlCommandTypes.Contains(typeName) && creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } sql && IsBuiltString(sql))
            {
                yield return CodeFinding(creation, model, module, file, "sql-built-from-strings", Severity.High,
                    $"{typeName} runs SQL built from strings",
                    "If any part comes from a request, an attacker can change the query (SQL injection).",
                    "Use command parameters (@name) instead of putting values into the SQL text.");
            }
        }

        foreach (var assignment in rootNode.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var target = assignment.Left.ToString();
            if ((target.EndsWith("ServerCertificateCustomValidationCallback", StringComparison.Ordinal) || target.EndsWith("RemoteCertificateValidationCallback", StringComparison.Ordinal))
                && (assignment.Right.ToString().Contains("DangerousAcceptAnyServerCertificateValidator", StringComparison.Ordinal) || ReturnsTrue(assignment.Right)))
            {
                yield return CodeFinding(assignment, model, module, file, "tls-validation-off", Severity.High,
                    "TLS certificate checks are turned off",
                    "Anyone between the app and the server can impersonate it and read or change the traffic.",
                    "Remove the callback; fix the certificate or trust store instead.");
            }
        }

        foreach (var literal in rootNode.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            if (literal.Token.ValueText is not { Length: >= 8 } value)
            {
                continue;
            }

            var holder = HolderName(literal);
            var looksSecret = SecretValue().IsMatch(value) || (holder is not null && SecretName().IsMatch(holder) && !value.Contains(' ', StringComparison.Ordinal) && !value.StartsWith("X-", StringComparison.Ordinal));
            if (looksSecret)
            {
                yield return CodeFinding(literal, model, module, file, "hardcoded-secret", module.IsTest ? Severity.Medium : Severity.High,
                    holder is null ? "A secret-looking value is written in the code" : $"'{holder}' holds a value written in the code",
                    "Anyone with the code (or a leaked copy) gets the secret, and it can't be rotated without a release.",
                    "Read it from configuration or a secret store; rotate the exposed value.");
            }
        }
    }

    /// <summary>A string made by concatenation or interpolation, rather than a constant.</summary>
    private static bool IsBuiltString(ExpressionSyntax expression) =>
        expression is InterpolatedStringExpressionSyntax
        || (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
        || (expression is InvocationExpressionSyntax call && MethodName(call) is "Format" or "Concat");

    private static bool ReturnsTrue(ExpressionSyntax expression) => expression switch
    {
        LambdaExpressionSyntax { ExpressionBody: LiteralExpressionSyntax lit } => lit.IsKind(SyntaxKind.TrueLiteralExpression),
        LambdaExpressionSyntax { Block: { } block } => block.Statements.Count == 1 && block.Statements[0] is ReturnStatementSyntax { Expression: LiteralExpressionSyntax r } && r.IsKind(SyntaxKind.TrueLiteralExpression),
        _ => false,
    };

    /// <summary>The variable, field, property or argument name a literal is assigned to, if any.</summary>
    private static string? HolderName(LiteralExpressionSyntax literal) => literal.Parent switch
    {
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v } => v.Identifier.Text,
        EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax p } => p.Identifier.Text,
        AssignmentExpressionSyntax a => a.Left.ToString().Split('.').Last(),
        _ => null,
    };

    private SecurityFinding CodeFinding(SyntaxNode at, SemanticModel model, Module module, string file, string rule, Severity severity, string title, string why, string fix)
    {
        var nodeId = EnclosingNodeId(at, model, module);
        return new SecurityFinding
        {
            Rule = rule, Severity = severity, NodeId = nodeId, Title = title, Why = why, Fix = fix,
            File = Rel(file), Line = at.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
        };
    }

    /// <summary>The method (or type, or startup, or module) a piece of code belongs to in the graph.</summary>
    private string EnclosingNodeId(SyntaxNode at, SemanticModel model, Module module)
    {
        foreach (var ancestor in at.AncestorsAndSelf())
        {
            var symbol = ancestor switch
            {
                MethodDeclarationSyntax m => model.GetDeclaredSymbol(m) as ISymbol,
                BaseTypeDeclarationSyntax t => model.GetDeclaredSymbol(t),
                _ => null,
            };
            if (IdOf(symbol) is { } id && _nodes.ContainsKey(id))
            {
                return id;
            }

            if (ancestor is GlobalStatementSyntax && _nodes.ContainsKey(StartupId(module)))
            {
                return StartupId(module);
            }
        }

        return module.Id;
    }

    private static SecurityFinding Finding(CodeNode node, string rule, Severity severity, string title, string why, string fix) => new()
    {
        Rule = rule, Severity = severity, NodeId = node.Id, Title = title, Why = why, Fix = fix, File = node.File, Line = node.StartLine,
    };
}
