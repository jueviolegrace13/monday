using System.Runtime.CompilerServices;
using Monday.Application.Abstractions;

namespace Monday.Domain.Tests.Fakes;

/// <summary>A model that replays scripted turns: each call yields the next turn and records the request it got.</summary>
internal sealed class ScriptedChatModel(params IReadOnlyList<ModelEvent>[] turns) : IChatModel
{
    private int _next;

    public List<ModelRequest> Requests { get; } = [];

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        if (_next >= turns.Length)
        {
            throw new InvalidOperationException("The scripted model has no more turns.");
        }

        foreach (var item in turns[_next++])
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}
