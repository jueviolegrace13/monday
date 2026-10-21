using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Audit;
using Monday.Domain.Common;
using Monday.Domain.Projects;
using Monday.Domain.Traffic;

namespace Monday.Application.Traffic;

/// <summary>What the agent sends per request. No body, query, header, IP or user name can be expressed here.</summary>
public sealed record TrafficUpload(string TraceId, DateTimeOffset At, string Method, string Route, int Status, double DurationMs, IReadOnlyList<string>? Guards, string? UserHash, string? ErrorType);

/// <summary>A request as a viewer sees it. The user hash is shown only to people allowed to follow one user.</summary>
public sealed record TrafficView(string TraceId, DateTimeOffset At, string Method, string Route, int Status, double DurationMs, Outcome Outcome, IReadOnlyList<string> Guards, string? UserHash, string? ErrorType, string NodeId)
{
    public static TrafficView Of(RequestTrace t, bool withUser) =>
        new(t.TraceId, t.At, t.Method, t.Route, t.Status, t.DurationMs, t.Outcome, t.Guards, withUser ? t.UserHash : null, t.ErrorType, t.GraphNodeId);
}

/// <summary>Stores requests and their live window; nothing older than the live window is ever removed, only moved to the archive.</summary>
public interface ITrafficStore
{
    Task AppendAsync(IReadOnlyList<RequestTrace> traces, CancellationToken ct);

    Task<IReadOnlyList<RequestTrace>> RangeAsync(ProjectId project, DateTimeOffset since, DateTimeOffset until, string? userHash, int limit, CancellationToken ct);

    /// <summary>The newest <paramref name="limit"/> requests in the window, newest first.</summary>
    Task<IReadOnlyList<RequestTrace>> NewestAsync(ProjectId project, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken ct);
}

/// <summary>Fans new requests out to everyone watching a project live.</summary>
public interface ITrafficFeed
{
    void Publish(ProjectId project, IReadOnlyList<RequestTrace> traces);

    IAsyncEnumerable<RequestTrace> SubscribeAsync(ProjectId project, CancellationToken ct);
}

public sealed class TrafficService(ITrafficStore store, ITrafficFeed feed, ProjectService projects, IAuditLog audit, IClock clock)
{
    private const int MaxBatch = 1000;
    private const int MaxRange = 5000;
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The agent's batch. Inbound only: Monday never calls back into the app, so this is allowed from
    /// production keys too. Requests timed in the future (clock skew) are pulled back to now.
    /// </summary>
    public async Task<int> IngestAsync(AppReporter reporter, IReadOnlyList<TrafficUpload> uploads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(uploads);
        Guard.That(uploads.Count is > 0 and <= MaxBatch, "traffic.batch_size", $"Send between 1 and {MaxBatch} requests at a time.");
        var now = clock.Now;
        var traces = uploads.Select(u => RequestTrace.Record(
            reporter.Project.Id, reporter.Environment.Id, u.TraceId, u.At > now + MaxSkew ? now : u.At,
            u.Method, u.Route, u.Status, u.DurationMs, u.Guards, u.UserHash, u.ErrorType)).ToList();
        await store.AppendAsync(traces, ct).ConfigureAwait(false);
        feed.Publish(reporter.Project.Id, traces);
        return traces.Count;
    }

    /// <summary>Totals for the header and alerts: anyone on the project may see them.</summary>
    public async Task<TrafficStats> StatsAsync(ProjectId id, TimeSpan window, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ViewTrafficTotals);
        Guard.That(window > TimeSpan.Zero && window <= TrafficRetention.Live, "traffic.window", "Pick a window between a second and 7 days.");
        var now = clock.Now;
        var traces = await store.RangeAsync(id, now - window, now, null, int.MaxValue, ct).ConfigureAwait(false);
        return TrafficStats.Of(traces, window);
    }

    /// <summary>
    /// Requests in a time window, oldest first, for replay. Following one user needs its own permission
    /// and every such look is written to the audit log.
    /// </summary>
    public async Task<IReadOnlyList<TrafficView>> ReplayAsync(ProjectId id, DateTimeOffset since, DateTimeOffset until, string? userId, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ReplayAppTraffic);
        Guard.That(since < until && until - since <= TimeSpan.FromDays(1), "traffic.range", "Replay at most one day at a time.");
        Guard.That(clock.Now - since <= TrafficRetention.Live, "traffic.archived", "That window is older than 7 days: restore it from the archive first.");

        string? userHash = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            project.EnsureCan(user, Permission.ReplayUserTraffic);
            userHash = project.HashUser(userId);
            await audit.AppendAsync(new AuditEntry(id, user, "traffic.user_replay", $"{since:u} – {until:u}", clock.Now), ct).ConfigureAwait(false);
        }

        var traces = await store.RangeAsync(id, since, until, userHash, MaxRange, ct).ConfigureAwait(false);
        var withUser = project.Can(user, Permission.ReplayUserTraffic);
        return traces.Select(t => TrafficView.Of(t, withUser)).ToList();
    }

    /// <summary>
    /// The newest requests in a time window, newest first. Same window rules as replay, and it needs request
    /// inspection as well (as the recent_requests tool does). It cannot follow one user, so there is no user-replay audit.
    /// </summary>
    public async Task<IReadOnlyList<TrafficView>> RecentAsync(ProjectId id, DateTimeOffset since, DateTimeOffset until, int limit, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ReplayAppTraffic);
        project.EnsureCan(user, Permission.InspectRequests);
        Guard.That(since < until && until - since <= TimeSpan.FromDays(1), "traffic.range", "Look at most one day at a time.");
        Guard.That(clock.Now - since <= TrafficRetention.Live, "traffic.archived", "That window is older than 7 days: restore it from the archive first.");
        Guard.That(limit is > 0 and <= MaxRange, "traffic.limit", $"Ask for between 1 and {MaxRange} requests.");

        var traces = await store.NewestAsync(id, since, until, limit, ct).ConfigureAwait(false);
        var withUser = project.Can(user, Permission.ReplayUserTraffic);
        return traces.Select(t => TrafficView.Of(t, withUser)).ToList();
    }

    /// <summary>The live stream for one viewer, filtered to what their role may see.</summary>
    public async Task<IAsyncEnumerable<TrafficView>> LiveAsync(ProjectId id, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ViewLiveGraph);
        var withUser = project.Can(user, Permission.ReplayUserTraffic);
        return Stream(feed.SubscribeAsync(id, ct), withUser);
    }

    private static async IAsyncEnumerable<TrafficView> Stream(IAsyncEnumerable<RequestTrace> source, bool withUser)
    {
        await foreach (var t in source.ConfigureAwait(false))
        {
            yield return TrafficView.Of(t, withUser);
        }
    }
}
