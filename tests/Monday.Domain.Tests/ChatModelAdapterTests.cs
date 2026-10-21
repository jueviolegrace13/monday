using System.Net;
using System.Text;
using System.Text.Json;
using Monday.Application.Abstractions;
using Monday.Application.Agent;
using Monday.Domain.Agent;
using Monday.Infrastructure.Models;

namespace Monday.Domain.Tests;

/// <summary>The two streaming adapters, against canned provider streams served by a stub handler.</summary>
internal static class ChatModelAdapterTests
{
    internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }

    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static ModelSettings Settings(ModelProvider provider, Uri? baseUrl = null) =>
        ModelSettings.Create(provider, "test-model", baseUrl, "v1:x:y:z", "abcd", null);

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""").RootElement.Clone();

    private static ModelRequest Request(params ChatMessage[] messages) =>
        new("be brief", messages, [new ToolSpec("find_nodes", "Find nodes.", Schema)]);

    internal static ModelRequest Simple() => Request(new ChatMessage(ChatRole.User, "hi"));

    internal static List<ModelEvent> Collect(IChatModel model, ModelRequest request)
    {
        var events = new List<ModelEvent>();
        var enumerator = model.StreamAsync(request, default).GetAsyncEnumerator();
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

    private static AnthropicChatModel Anthropic(StubHandler handler) =>
        new(new HttpClient(handler), Settings(ModelProvider.Anthropic), "sk-ant-secret-key");

    private static OpenAiCompatibleChatModel OpenAi(StubHandler handler, Uri? baseUrl = null) =>
        new(new HttpClient(handler), Settings(ModelProvider.OpenAiCompatible, baseUrl), "sk-oai-secret-key");

    public static void AnthropicStreamsTextThenToolCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("anthropic-tool-stream.txt"));
        var events = Collect(Anthropic(handler), Simple());

        Assert.Equal(4, events.Count);
        Assert.Equal("Let me ", ((TextDelta)events[0]).Text);
        Assert.Equal("look.", ((TextDelta)events[1]).Text);
        var call = (ToolCall)events[2];
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("find_nodes", call.Name);
        Assert.Equal("Orders", call.Args.GetProperty("query").GetString());
        var done = (Finished)events[3];
        Assert.Equal(StopReason.ToolUse, done.Reason);
        Assert.Equal(new Usage(25, 42), done.Usage);
        Assert.Equal("sk-ant-secret-key", handler.Request!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request.RequestUri!.ToString());
    }

    public static void OpenAiStreamsTextThenToolCallWithUsage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("openai-tool-stream.txt"));
        var events = Collect(OpenAi(handler), Simple());

        Assert.Equal("Looking", ((TextDelta)events[0]).Text);
        var call = (ToolCall)events[2];
        Assert.Equal("call_1", call.Id);
        Assert.Equal("Orders", call.Args.GetProperty("query").GetString());
        var done = (Finished)events[3];
        Assert.Equal(StopReason.ToolUse, done.Reason);
        Assert.Equal(new Usage(30, 12), done.Usage);
        Assert.Equal("Bearer sk-oai-secret-key", handler.Request!.Headers.Authorization!.ToString());
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Request.RequestUri!.ToString());
    }

    public static void OpenAiUsesTheBaseUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("openai-tool-stream.txt"));
        _ = Collect(OpenAi(handler, new Uri("https://models.example.com/v1/")), Simple());

        Assert.Equal("https://models.example.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
    }

    public static void OpenAiJoinsFragmentedArguments()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("openai-fragmented-args.txt"));
        var events = Collect(OpenAi(handler), Simple());

        var calls = events.OfType<ToolCall>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("Orders", calls[0].Args.GetProperty("query").GetString());
        Assert.Equal("n1", calls[1].Args.GetProperty("id").GetString());
        Assert.Equal("node_detail", calls[1].Name);
        var done = (Finished)events[^1];
        Assert.Equal(new Usage(7, 9), done.Usage);
    }

    public static void OpenAiBadArgumentsMarkedInvalid()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("openai-bad-args.txt"));
        var events = Collect(OpenAi(handler), Simple());

        var call = events.OfType<ToolCall>().Single();
        Assert.Equal("{\"query\": \"Ord", call.Args.GetProperty("_invalid").GetString());
        Assert.Equal(new Usage(0, 0), ((Finished)events[^1]).Usage);
    }

    private static void AssertFails(IChatModel model, string code, string secret)
    {
        try
        {
            _ = Collect(model, Simple());
        }
        catch (ModelException ex)
        {
            Assert.Equal(code, ex.Code);
            Assert.True(!ex.Message.Contains(secret, StringComparison.Ordinal), "message must not hold the key");
            return;
        }

        throw new InvalidOperationException($"Expected ModelException {code}.");
    }

    public static void UnauthorizedMapsToModelAuth()
    {
        AssertFails(Anthropic(new StubHandler(HttpStatusCode.Unauthorized, "{}")), "model.auth", "sk-ant-secret-key");
        AssertFails(OpenAi(new StubHandler(HttpStatusCode.Forbidden, "{}")), "model.auth", "sk-oai-secret-key");
    }

    public static void RateLimitMapsToRateLimited()
    {
        AssertFails(Anthropic(new StubHandler(HttpStatusCode.TooManyRequests, "{}")), "model.rate_limited", "sk-ant-secret-key");
        AssertFails(OpenAi(new StubHandler(HttpStatusCode.TooManyRequests, "{}")), "model.rate_limited", "sk-oai-secret-key");
    }

    public static void ServerErrorMapsToUnavailable()
    {
        AssertFails(Anthropic(new StubHandler(HttpStatusCode.BadGateway, "{}")), "model.unavailable", "sk-ant-secret-key");
        AssertFails(OpenAi(new StubHandler(HttpStatusCode.InternalServerError, "{}")), "model.unavailable", "sk-oai-secret-key");
    }

    public static void RequestCarriesToolsInProviderShape()
    {
        var anthropic = new StubHandler(HttpStatusCode.OK, Fixture("anthropic-tool-stream.txt"));
        _ = Collect(Anthropic(anthropic), Simple());
        using var a = JsonDocument.Parse(anthropic.RequestBody!);
        var aTool = a.RootElement.GetProperty("tools")[0];
        Assert.Equal("find_nodes", aTool.GetProperty("name").GetString());
        Assert.Equal("Find nodes.", aTool.GetProperty("description").GetString());
        Assert.Equal("object", aTool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal(4096, a.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(a.RootElement.GetProperty("stream").GetBoolean(), "stream");
        Assert.Equal("be brief", a.RootElement.GetProperty("system").GetString());

        var openai = new StubHandler(HttpStatusCode.OK, Fixture("openai-tool-stream.txt"));
        _ = Collect(OpenAi(openai), Simple());
        using var o = JsonDocument.Parse(openai.RequestBody!);
        var oTool = o.RootElement.GetProperty("tools")[0];
        Assert.Equal("function", oTool.GetProperty("type").GetString());
        Assert.Equal("find_nodes", oTool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", oTool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.True(o.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(), "include_usage");
        Assert.Equal("system", o.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    private static ModelRequest WithToolRound() => Request(
        new ChatMessage(ChatRole.User, "find orders"),
        new ChatMessage(ChatRole.Assistant, "On it.", [new ToolCallPart("c1", "find_nodes", JsonDocument.Parse("""{"query":"Orders"}""").RootElement.Clone())]),
        new ChatMessage(ChatRole.Tool, null, null, new ToolResultPart("c1", JsonDocument.Parse("""{"nodes":[]}""").RootElement.Clone(), IsError: true)));

    public static void AnthropicMapsToolHistoryToBlocks()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("anthropic-tool-stream.txt"));
        _ = Collect(Anthropic(handler), WithToolRound());
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var messages = doc.RootElement.GetProperty("messages");

        Assert.Equal(3, messages.GetArrayLength());
        var use = messages[1].GetProperty("content")[1];
        Assert.Equal("tool_use", use.GetProperty("type").GetString());
        Assert.Equal("c1", use.GetProperty("id").GetString());
        Assert.Equal("Orders", use.GetProperty("input").GetProperty("query").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("c1", result.GetProperty("tool_use_id").GetString());
        Assert.True(result.GetProperty("is_error").GetBoolean(), "is_error");
    }

    public static void OpenAiMapsToolHistoryToToolMessages()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("openai-tool-stream.txt"));
        _ = Collect(OpenAi(handler), WithToolRound());
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var messages = doc.RootElement.GetProperty("messages");

        var assistant = messages[2];
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("c1", call.GetProperty("id").GetString());
        Assert.Equal("""{"query":"Orders"}""", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("c1", messages[3].GetProperty("tool_call_id").GetString());
    }

    public static void FactoryRefusesGeminiForNow()
    {
        var factory = new ChatModelFactory(new SingleClientFactory());
        var ex = Assert.Throws<ModelException>(() => factory.Create(Settings(ModelProvider.Gemini), "key-12345678"));
        Assert.Equal("model.unsupported", ex.Code);
        Assert.True(factory.Create(Settings(ModelProvider.Anthropic), "key-12345678") is AnthropicChatModel, "anthropic");
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
