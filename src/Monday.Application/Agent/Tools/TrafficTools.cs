using System.Text.Json;
using Monday.Application.Abstractions;
using Monday.Application.Traffic;
using Monday.Domain.Access;
using Monday.Domain.Common;

namespace Monday.Application.Agent.Tools;

public sealed class TrafficStatsTool(TrafficService traffic) : IAgentTool
{
    public string Name => "traffic_stats";

    public string Description => "Totals for a recent window: request count, per second, p50 and p95 latency, error rate and outcomes. Default window is the last hour.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{"windowMinutes":{"type":"integer","minimum":1,"maximum":10080,"description":"How far back to look. Default 60."}},
         "additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk, Permission.ViewTrafficTotals };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var window = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("windowMinutes", out var m) && m.TryGetInt32(out var minutes)
            ? TimeSpan.FromMinutes(minutes)
            : ToolInput.DefaultWindow;
        var stats = await traffic.StatsAsync(context.Project, window, context.User, ct).ConfigureAwait(false);
        return new ToolResult(ToolInput.Data(new { windowMinutes = (int)window.TotalMinutes, stats }), []);
    }
}

public sealed class RecentRequestsTool(TrafficService traffic, IClock clock) : IAgentTool
{
    public string Name => "recent_requests";

    public string Description => "The most recent requests (method, route template, status, duration, outcome, guards), the newest 50 of the window, oldest first. Default window is the last hour; at most 50 are returned. Never any body, query or IP.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{
          "since":{"type":"string","description":"ISO time. Default: one hour before until."},
          "until":{"type":"string","description":"ISO time. Default: now."}},
         "additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk, Permission.InspectRequests };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (since, until) = ToolInput.Window(input, clock.Now);
        var newest = await traffic.RecentAsync(context.Project, since, until, ToolInput.MaxItems + 1, context.User, ct).ConfigureAwait(false);
        var truncated = newest.Count > ToolInput.MaxItems;

        // The service already hides the hash from people who may not follow users; this keeps that true for the role asking.
        var withUser = RolePermissions.Allows(context.Role, Permission.ReplayUserTraffic);
        var page = newest.Take(ToolInput.MaxItems).Reverse().Select(t => withUser ? t : t with { UserHash = null }).ToList();
        return new ToolResult(
            ToolInput.Data(new { requests = page, truncated }),
            [.. page.Select(t => t.NodeId).Distinct()]);
    }
}
