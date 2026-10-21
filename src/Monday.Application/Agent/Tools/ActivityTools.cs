using System.Text.Json;
using Monday.Application.Abstractions;
using Monday.Application.Activity;
using Monday.Domain.Access;

namespace Monday.Application.Agent.Tools;

public sealed class RecentActivityTool(ActivityService activity, IClock clock) : IAgentTool
{
    public string Name => "recent_activity";

    public string Description => "What Claude and other actors did on the project: one line per event with the graph nodes it touched. Default window is the last hour; at most 50, newest last.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{
          "since":{"type":"string","description":"ISO time. Default: one hour before until."},
          "until":{"type":"string","description":"ISO time. Default: now."}},
         "additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk, Permission.ViewLiveGraph };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (since, until) = ToolInput.Window(input, clock.Now);
        var all = await activity.RangeAsync(context.Project, since, until, context.User, ct).ConfigureAwait(false);
        var page = all.OrderBy(e => e.At).TakeLast(ToolInput.MaxItems).ToList();
        var events = page.Select(e => new { e.At, e.SessionId, e.Kind, e.Summary, e.NodeIds });
        return new ToolResult(
            ToolInput.Data(new { events, total = all.Count, truncated = all.Count > page.Count }),
            [.. page.SelectMany(e => e.NodeIds).Distinct()]);
    }
}
