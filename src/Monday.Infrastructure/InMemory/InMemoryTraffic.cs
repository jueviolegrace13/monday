using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Monday.Application.Traffic;
using Monday.Domain.Projects;
using Monday.Domain.Traffic;

namespace Monday.Infrastructure.InMemory;

public sealed class InMemoryTrafficStore : ITrafficStore
{
    private readonly ConcurrentDictionary<ProjectId, List<RequestTrace>> _byProject = new();

    public Task AppendAsync(IReadOnlyList<RequestTrace> traces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(traces);
        foreach (var group in traces.GroupBy(t => t.ProjectId))
        {
            var list = _byProject.GetOrAdd(group.Key, _ => []);
            lock (list)
            {
                list.AddRange(group);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RequestTrace>> RangeAsync(ProjectId project, DateTimeOffset since, DateTimeOffset until, string? userHash, int limit, CancellationToken ct)
    {
        if (!_byProject.TryGetValue(project, out var list))
        {
            return Task.FromResult<IReadOnlyList<RequestTrace>>([]);
        }

        lock (list)
        {
            return Task.FromResult<IReadOnlyList<RequestTrace>>(list
                .Where(t => t.At >= since && t.At <= until && (userHash is null || t.UserHash == userHash))
                .OrderBy(t => t.At).Take(limit).ToList());
        }
    }
}

/// <summary>Broadcasts new requests to every live viewer of a project. A slow viewer drops its oldest, never blocks ingest.</summary>
public sealed class TrafficFeed : ITrafficFeed
{
    private readonly ConcurrentDictionary<ProjectId, ConcurrentDictionary<Guid, Channel<RequestTrace>>> _subscribers = new();

    public void Publish(ProjectId project, IReadOnlyList<RequestTrace> traces)
    {
        ArgumentNullException.ThrowIfNull(traces);
        if (!_subscribers.TryGetValue(project, out var subs))
        {
            return;
        }

        foreach (var channel in subs.Values)
        {
            foreach (var t in traces)
            {
                channel.Writer.TryWrite(t);
            }
        }
    }

    public async IAsyncEnumerable<RequestTrace> SubscribeAsync(ProjectId project, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<RequestTrace>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        var subs = _subscribers.GetOrAdd(project, _ => new());
        subs[id] = channel;
        try
        {
            await foreach (var t in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return t;
            }
        }
        finally
        {
            subs.TryRemove(id, out _);
        }
    }
}
