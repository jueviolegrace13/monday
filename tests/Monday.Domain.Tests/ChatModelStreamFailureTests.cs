using System.Net;
using System.Text;
using Monday.Application.Abstractions;
using Monday.Domain.Agent;
using Monday.Infrastructure.Models;

namespace Monday.Domain.Tests;

/// <summary>Streams that hang, truncate, error or arrive malformed must fail loudly, never look like an answer.</summary>
internal static class ChatModelStreamFailureTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(150);

    /// <summary>Serves a canned prefix, then either hangs, throws an IOException, or ends.</summary>
    private sealed class ScriptedStream(string prefix, Func<CancellationToken, Task>? then = null) : Stream
    {
        private readonly MemoryStream _head = new(Encoding.UTF8.GetBytes(prefix));

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _head.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0 || then is null)
            {
                return n;
            }

            await then(cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class StreamHandler(Func<Stream> open) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(open()) });
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }

    private static AnthropicChatModel Anthropic(HttpMessageHandler handler) =>
        new(new HttpClient(handler), ChatModelAdapterTests.Settings(ModelProvider.Anthropic), "sk-ant-secret-key", Idle);

    private static OpenAiCompatibleChatModel OpenAi(HttpMessageHandler handler) =>
        new(new HttpClient(handler), ChatModelAdapterTests.Settings(ModelProvider.OpenAiCompatible), "sk-oai-secret-key", Idle);

    private static ModelException Fails(IChatModel model)
    {
        try
        {
            _ = ChatModelAdapterTests.Collect(model, ChatModelAdapterTests.Simple());
        }
        catch (ModelException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected a ModelException.");
    }

    private static StreamHandler Serving(string body, Func<CancellationToken, Task>? then = null) => new(() => new ScriptedStream(body, then));

    private const string AnthropicHead = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1}}}\n\n";

    private const string OpenAiHead = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n";

    public static void HungAnthropicStreamTimesOutAsUnavailable()
    {
        var ex = Fails(Anthropic(Serving(AnthropicHead, ct => Task.Delay(Timeout.Infinite, ct))));
        Assert.Equal("model.unavailable", ex.Code);
    }

    public static void HungOpenAiStreamTimesOutAsUnavailable()
    {
        var ex = Fails(OpenAi(Serving(OpenAiHead, ct => Task.Delay(Timeout.Infinite, ct))));
        Assert.Equal("model.unavailable", ex.Code);
    }

    public static void AnthropicStreamWithoutMessageStopIsUnavailable()
    {
        var ex = Fails(Anthropic(Serving(AnthropicHead)));
        Assert.Equal("model.unavailable", ex.Code);
    }

    public static void OpenAiStreamWithoutFinishReasonIsUnavailable()
    {
        Assert.Equal("model.unavailable", Fails(OpenAi(Serving(OpenAiHead))).Code);
        Assert.Equal("model.unavailable", Fails(OpenAi(Serving(OpenAiHead + "data: [DONE]\n\n"))).Code);
    }

    public static void OpenAiErrorChunkIsUnavailable()
    {
        var ex = Fails(OpenAi(Serving("data: {\"error\":{\"message\":\"overloaded\",\"type\":\"server_error\"}}\n\n")));
        Assert.Equal("model.unavailable", ex.Code);
        Assert.True(!ex.Message.Contains("overloaded", StringComparison.Ordinal), "provider text is not echoed");
    }

    public static void MalformedDataIsUnavailable()
    {
        Assert.Equal("model.unavailable", Fails(Anthropic(Serving("data: not json\n\n"))).Code);
        Assert.Equal("model.unavailable", Fails(OpenAi(Serving("data: not json\n\n"))).Code);
        // Valid JSON of the wrong shape (a missing index) must not escape as KeyNotFound / InvalidOperation.
        Assert.Equal("model.unavailable", Fails(Anthropic(Serving("data: {\"type\":\"content_block_stop\"}\n\n"))).Code);
    }

    public static void MidStreamIoFailureIsUnavailable()
    {
        var ex = Fails(OpenAi(Serving(OpenAiHead, _ => throw new IOException("connection reset"))));
        Assert.Equal("model.unavailable", ex.Code);
    }

    public static void BadRequestMapsToRejected()
    {
        var handler = new ChatModelAdapterTests.StubHandler(HttpStatusCode.BadRequest, "{}");
        Assert.Equal("model.rejected", Fails(Anthropic(handler)).Code);
        Assert.Equal("model.rejected", Fails(OpenAi(handler)).Code);
    }

    public static void WrappedUnsafeEndpointIsUnwrapped()
    {
        var wrapped = new HttpRequestException("blocked", new UnsafeEndpointException("model.base_url_private", "private"));
        try
        {
            _ = ChatModelAdapterTests.Collect(OpenAi(new ThrowingHandler(wrapped)), ChatModelAdapterTests.Simple());
        }
        catch (UnsafeEndpointException ex)
        {
            Assert.Equal("model.base_url_private", ex.Code);
            return;
        }

        throw new InvalidOperationException("Expected UnsafeEndpointException.");
    }

    public static void AnthropicSkipsEmptyAssistantTurns()
    {
        var handler = new ChatModelAdapterTests.StubHandler(HttpStatusCode.OK, ChatModelAdapterTests.Fixture("anthropic-tool-stream.txt"));
        var model = new AnthropicChatModel(new HttpClient(handler), ChatModelAdapterTests.Settings(ModelProvider.Anthropic), "sk-ant-secret-key");
        var request = new ModelRequest("x", [new ChatMessage(ChatRole.User, "a"), new ChatMessage(ChatRole.Assistant, ""), new ChatMessage(ChatRole.User, "b")], []);
        _ = ChatModelAdapterTests.Collect(model, request);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength());
    }

    public static void OpenAiGeneratesIdForToolCallWithoutOne()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"find_nodes\",\"arguments\":\"{}\"}}]}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n";
        var call = ChatModelAdapterTests.Collect(OpenAi(Serving(body)), ChatModelAdapterTests.Simple()).OfType<ToolCall>().Single();
        Assert.True(call.Id.Length > 0, "an id is generated");
    }

    private static List<(string Event, string Data)> Read(string text)
    {
        var events = new List<(string, string)>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var e = SseReader.ReadAsync(stream, default).GetAsyncEnumerator();
        while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            events.Add(e.Current);
        }

        return events;
    }

    public static void SseJoinsMultiLineData()
    {
        var events = Read("event: x\ndata: a\ndata: b\n\n");
        Assert.Equal(1, events.Count);
        Assert.Equal("x", events[0].Event);
        Assert.Equal("a\nb", events[0].Data);
    }

    public static void SseHandlesCrlf()
    {
        var events = Read("event: x\r\ndata: a\r\n\r\ndata: b\r\n\r\n");
        Assert.Equal(2, events.Count);
        Assert.Equal("a", events[0].Data);
        Assert.Equal("b", events[1].Data);
    }

    public static void SseStripsOnlyOneLeadingSpace()
    {
        var events = Read("data:a\n\ndata:  b\n\n");
        Assert.Equal("a", events[0].Data);
        Assert.Equal(" b", events[1].Data);
    }

    public static void SseSkipsCommentsAndKeepsFinalUnterminatedEvent()
    {
        var events = Read(": ping\ndata: a\n\n: keep-alive\ndata: last");
        Assert.Equal(2, events.Count);
        Assert.Equal("a", events[0].Data);
        Assert.Equal("last", events[1].Data);
    }
}
