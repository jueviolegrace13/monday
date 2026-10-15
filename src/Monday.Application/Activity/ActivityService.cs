using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Activity;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Activity;

public sealed record ActivityUpload(string SessionId, DateTimeOffset? At, ActivityKind Kind, string Summary, IReadOnlyList<string>? NodeIds);

/// <summary>Claude's working sessions report what they do; the viewer plays it live and back.</summary>
public sealed class ActivityService(IActivityStore store, ProjectService projects, IClock clock)
{
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(7);

    public async Task<int> RecordAsync(AppReporter reporter, IReadOnlyList<ActivityUpload> uploads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(uploads);
        Guard.That(uploads.Count is > 0 and <= 500, "activity.batch_size", "Send between 1 and 500 events at a time.");
        var now = clock.Now;

        // A reporter may batch events it saw earlier, but cannot write into the future.
        var events = uploads
            .Select(u => ActivityEvent.Record(reporter.Project.Id, u.SessionId, Min(u.At ?? now, now + MaxClockSkew), u.Kind, u.Summary, u.NodeIds))
            .ToList();
        await store.AppendAsync(events, ct).ConfigureAwait(false);
        return events.Count;
    }

    public async Task<IReadOnlyList<ActivityEvent>> RangeAsync(ProjectId id, DateTimeOffset from, DateTimeOffset to, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ViewLiveGraph);
        Guard.That(from <= to && to - from <= MaxRange, "activity.range", "Ask for at most 7 days at a time.");
        return await store.RangeAsync(id, from, to, ct).ConfigureAwait(false);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
