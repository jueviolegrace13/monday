using Monday.Application.Abstractions;
using Monday.Domain.Tests.Fakes;

namespace Monday.Domain.Tests;

/// <summary>The fake model that later loop tests rely on replays its turns in order.</summary>
internal static class ScriptedChatModelTests
{
    private static List<ModelEvent> Collect(ScriptedChatModel model, ModelRequest request)
    {
        var events = new List<ModelEvent>();
        var stream = model.StreamAsync(request, default);
        var enumerator = stream.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                events.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return events;
    }

    public static void ScriptedModelReplaysTurnsAndRecordsRequests()
    {
        var first = new ModelEvent[] { new TextDelta("hi"), new Finished(StopReason.EndTurn, new Usage(1, 2)) };
        var second = new ModelEvent[] { new TextDelta("again"), new Finished(StopReason.EndTurn, new Usage(3, 4)) };
        var model = new ScriptedChatModel(first, second);
        var one = new ModelRequest("be brief", [new ChatMessage(ChatRole.User, "one")], []);
        var two = new ModelRequest("be brief", [new ChatMessage(ChatRole.User, "two")], []);

        var a = Collect(model, one);
        var b = Collect(model, two);

        Assert.Equal("hi", ((TextDelta)a[0]).Text);
        Assert.Equal("again", ((TextDelta)b[0]).Text);
        Assert.Equal(2, model.Requests.Count);
        Assert.True(ReferenceEquals(model.Requests[0], one) && ReferenceEquals(model.Requests[1], two), "requests recorded in order");
    }
}
