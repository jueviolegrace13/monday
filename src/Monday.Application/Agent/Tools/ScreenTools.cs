using System.Text.Json;
using Monday.Application.CodeGraph;
using Monday.Application.Projects;
using Monday.Application.Traffic;
using Monday.Domain.Access;
using Monday.Domain.Common;

namespace Monday.Application.Agent.Tools;

/// <summary>Checks that the nodes a screen action names exist in the latest graph the asker may see.</summary>
internal static class ScreenNodes
{
    public static async Task EnsureExistAsync(GraphService graph, ToolContext context, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var view = await graph.GetAsync(context.Project, context.User, ct).ConfigureAwait(false) ?? throw new NotFoundException("graph");
        var known = view.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.FirstOrDefault(id => !known.Contains(id)) is { } missing)
        {
            throw new NotFoundException($"node '{missing}'");
        }
    }
}

public sealed class FocusNodeTool(GraphService graph) : IAgentTool
{
    public string Name => "focus_node";

    public string Description => "Move the viewer's focus to one node in the graph.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{"nodeId":{"type":"string"}},"required":["nodeId"],"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.ScreenAction;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAct };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var nodeId = ToolInput.RequiredString(input, "nodeId");
        await ScreenNodes.EnsureExistAsync(graph, context, [nodeId], ct).ConfigureAwait(false);
        return new ToolResult(ToolInput.Data(new { focused = nodeId }), [nodeId], new ScreenAction(Name, ToolInput.Data(new { nodeId })));
    }
}

public sealed class HighlightPathTool(GraphService graph) : IAgentTool
{
    public string Name => "highlight_path";

    public string Description => "Light up a path of nodes in the graph, in order (for example an endpoint and the methods it reaches). Two to 50 node ids.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{"nodeIds":{"type":"array","minItems":2,"maxItems":50,"items":{"type":"string"}}},
         "required":["nodeIds"],"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.ScreenAction;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAct };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!input.TryGetProperty("nodeIds", out var raw) || raw.ValueKind != JsonValueKind.Array)
        {
            throw ToolInput.Invalid("'nodeIds' must be a list.");
        }

        var ids = raw.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw ToolInput.Invalid("'nodeIds' holds text.")).ToList();
        Guard.That(ids.Count is >= 2 and <= ToolInput.MaxItems, "agent.input", "Name between 2 and 50 nodes.");
        await ScreenNodes.EnsureExistAsync(graph, context, ids, ct).ConfigureAwait(false);
        return new ToolResult(ToolInput.Data(new { highlighted = ids }), ids, new ScreenAction(Name, ToolInput.Data(new { nodeIds = ids })));
    }
}

public sealed class SetLensTool : IAgentTool
{
    private static readonly string[] Lenses = ["structure", "security", "coverage", "live"];

    public string Name => "set_lens";

    public string Description => "Switch the graph's lens: structure, security (threats), coverage (tests) or live (requests moving).";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{"lens":{"type":"string","enum":["structure","security","coverage","live"]}},
         "required":["lens"],"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.ScreenAction;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAct };

    public ToolChannel? OnlyOn => null;

    public Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        var lens = ToolInput.RequiredString(input, "lens").ToLowerInvariant();
        Guard.That(Lenses.Contains(lens), "agent.input", "The lens is one of structure, security, coverage or live.");
        return Task.FromResult(new ToolResult(ToolInput.Data(new { lens }), [], new ScreenAction(Name, ToolInput.Data(new { lens }))));
    }
}

/// <summary>
/// Plays a window of traffic on the graph. The only screen tool that touches the service layer for data: it goes
/// through <see cref="TrafficService.ReplayAsync"/>, so replay permission and the audit of user replays apply.
/// </summary>
public sealed class StartReplayTool(TrafficService traffic, GraphService graph) : IAgentTool
{
    public string Name => "start_replay";

    public string Description => "Replay a window of traffic (at most one day, within 7 days) on the graph; with userId, follow one user's requests (audited).";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{
          "since":{"type":"string","description":"ISO time."},
          "until":{"type":"string","description":"ISO time."},
          "userId":{"type":"string","description":"Follow only this user. Needs permission to replay user traffic."}},
         "required":["since","until"],"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.ScreenAction;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAct, Permission.ReplayAppTraffic };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var since = ToolInput.Time(input, "since") ?? throw ToolInput.Invalid("'since' is required.");
        var until = ToolInput.Time(input, "until") ?? throw ToolInput.Invalid("'until' is required.");
        var userId = ToolInput.String(input, "userId");

        var requests = await traffic.ReplayAsync(context.Project, since, until, userId, context.User, ct).ConfigureAwait(false);
        var view = await graph.GetAsync(context.Project, context.User, ct).ConfigureAwait(false);
        var known = view?.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var ids = requests.Select(r => r.NodeId).Where(known.Contains).Distinct().Take(ToolInput.MaxItems).ToList();

        var args = userId is null ? ToolInput.Data(new { since, until }) : ToolInput.Data(new { since, until, userId });
        return new ToolResult(ToolInput.Data(new { requests = requests.Count, since, until, user = userId is not null }), ids, new ScreenAction(Name, args));
    }
}
