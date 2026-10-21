using System.Text.Json;
using Monday.Application.CodeGraph;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.CodeGraph;
using Monday.Domain.Common;

namespace Monday.Application.Agent.Tools;

/// <summary>The shape of a node without its source, which only ever travels through <see cref="NodeDetailTool"/>.</summary>
internal static class NodeSummary
{
    public static object Of(CodeNode n) => new { n.Id, n.Kind, n.Name, n.File, n.Route, n.Guards };
}

public sealed class GraphOverviewTool(GraphService graph) : IAgentTool
{
    public string Name => "graph_overview";

    public string Description => "The shape of the app's code graph at its latest commit: how many nodes of each kind, finding counts by severity, and the top-level modules.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""{"type":"object","properties":{},"additionalProperties":false}""");

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk, Permission.ViewLiveGraph };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var view = await graph.GetAsync(context.Project, context.User, ct).ConfigureAwait(false);
        if (view is null)
        {
            return new ToolResult(ToolInput.Data(new { indexed = false }), []);
        }

        var top = view.Nodes.Where(n => n.Kind is NodeKind.App or NodeKind.Module or NodeKind.Feature).Take(ToolInput.MaxItems).ToList();
        var byKind = view.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count());
        var data = new
        {
            indexed = true,
            view.Commit,
            view.IndexedAt,
            nodeCount = view.Nodes.Count,
            edgeCount = view.Edges.Count,
            nodesByKind = byKind,
            findingCounts = view.FindingCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            topLevel = top.Select(NodeSummary.Of),
        };
        return new ToolResult(ToolInput.Data(data), [.. top.Select(n => n.Id)]);
    }
}

public sealed class FindNodesTool(GraphService graph) : IAgentTool
{
    public string Name => "find_nodes";

    public string Description => "Find graph nodes whose id, name or route contains the text, optionally of one kind. At most 50 are returned.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Text to look for in a node's id, name or route."},
          "kind":{"type":"string","enum":["App","Module","Feature","Type","Method","Endpoint","Test","Middleware","RouteGroup"]}},
         "required":["query"],"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk, Permission.ViewLiveGraph };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var query = ToolInput.RequiredString(input, "query");
        NodeKind? kind = ToolInput.String(input, "kind") is { } k && Enum.TryParse<NodeKind>(k, true, out var parsed) ? parsed : null;
        var view = await graph.GetAsync(context.Project, context.User, ct).ConfigureAwait(false);
        var matches = (view?.Nodes ?? [])
            .Where(n => (kind is null || n.Kind == kind)
                && (Has(n.Id, query) || Has(n.Name, query) || Has(n.Route, query)))
            .ToList();
        var page = matches.Take(ToolInput.MaxItems).ToList();
        return new ToolResult(
            ToolInput.Data(new { nodes = page.Select(NodeSummary.Of), total = matches.Count, truncated = matches.Count > page.Count }),
            [.. page.Select(n => n.Id)]);
    }

    private static bool Has(string? text, string query) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class NodeDetailTool(GraphService graph) : IAgentTool
{
    public string Name => "node_detail";

    public string Description => "One node with its callers, callees, covering tests and endpoints. Source code and Claude's explanation are included only for people allowed to view code.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{"nodeId":{"type":"string","description":"A node id from find_nodes or graph_overview."}},
         "required":["nodeId"],"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk, Permission.ViewLiveGraph };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var nodeId = ToolInput.RequiredString(input, "nodeId");
        return RolePermissions.Allows(context.Role, Permission.ViewCode)
            ? await WithCodeAsync(context, nodeId, ct).ConfigureAwait(false)
            : await ShapeOnlyAsync(context, nodeId, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> WithCodeAsync(ToolContext context, string nodeId, CancellationToken ct)
    {
        var detail = await graph.GetNodeAsync(context.Project, nodeId, context.User, ct).ConfigureAwait(false);
        var code = detail.Node.Code;
        var truncated = code is { Length: > ToolInput.MaxCodeChars };
        var data = new
        {
            node = detail.Node with { Code = truncated ? code![..ToolInput.MaxCodeChars] : code },
            codeTruncated = truncated,
            explanation = detail.Explanation,
            explanationIsCurrent = detail.ExplanationIsCurrent,
            callers = Summaries(detail.Callers),
            callees = Summaries(detail.Callees),
            tests = Summaries(detail.Tests),
            endpoints = Summaries(detail.Endpoints),
        };
        var ids = new[] { detail.Node }.Concat(detail.Callers).Concat(detail.Callees).Concat(detail.Tests).Concat(detail.Endpoints).Select(n => n.Id).Distinct();
        return new ToolResult(ToolInput.Data(data), [.. ids]);
    }

    // Without ViewCode the service gives the graph already stripped of source, so shape questions still work.
    private async Task<ToolResult> ShapeOnlyAsync(ToolContext context, string nodeId, CancellationToken ct)
    {
        var view = await graph.GetAsync(context.Project, context.User, ct).ConfigureAwait(false) ?? throw new NotFoundException("graph");
        var node = view.Nodes.FirstOrDefault(n => n.Id == nodeId) ?? throw new NotFoundException("node");
        var byId = view.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        IReadOnlyList<CodeNode> Related(Func<CodeEdge, bool> match, Func<CodeEdge, string> pick) =>
            [.. view.Edges.Where(match).Select(pick).Distinct().Select(id => byId.GetValueOrDefault(id)).OfType<CodeNode>()];

        var callers = Related(e => e.Kind == EdgeKind.Calls && e.To == nodeId, e => e.From);
        var callees = Related(e => e.Kind == EdgeKind.Calls && e.From == nodeId, e => e.To);
        var tests = Related(e => e.Kind == EdgeKind.Covers && e.To == nodeId, e => e.From);
        var data = new { node, codeTruncated = false, callers = Summaries(callers), callees = Summaries(callees), tests = Summaries(tests) };
        var ids = new[] { node }.AsEnumerable().Concat(callers).Concat(callees).Concat(tests).Select(n => n.Id).Distinct();
        return new ToolResult(ToolInput.Data(data), [.. ids]);
    }

    private static IEnumerable<object> Summaries(IEnumerable<CodeNode> nodes) => nodes.Take(ToolInput.MaxItems).Select(NodeSummary.Of);
}

public sealed class SecurityFindingsTool(GraphService graph) : IAgentTool
{
    public string Name => "security_findings";

    public string Description => "Security findings the indexer raised on the latest commit, worst first, with why they matter and how to fix them. At most 50.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{"severity":{"type":"string","enum":["Low","Medium","High"],"description":"Only findings of this severity."}},
         "additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk, Permission.ViewCode };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        Severity? severity = ToolInput.String(input, "severity") is { } s && Enum.TryParse<Severity>(s, true, out var parsed) ? parsed : null;
        var view = await graph.GetAsync(context.Project, context.User, ct).ConfigureAwait(false);
        var matches = (view?.Findings ?? []).Where(f => severity is null || f.Severity == severity).ToList();
        var page = matches.Take(ToolInput.MaxItems).ToList();
        var data = new
        {
            findings = page,
            total = matches.Count,
            truncated = matches.Count > page.Count,
            counts = (view?.FindingCounts ?? new Dictionary<Severity, int>()).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        };
        return new ToolResult(ToolInput.Data(data), [.. page.Select(f => f.NodeId).Distinct()]);
    }
}
