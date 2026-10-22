using System.Net;
using System.Text;
using System.Text.Json;
using Monday.Application.Abstractions;
using Monday.Application.Agent;
using Monday.Domain.Agent;
using Monday.Infrastructure.Models;

namespace Monday.Domain.Tests;

/// <summary>The streaming adapters, against canned provider streams served by a stub handler.</summary>
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

    public static void FactoryCreatesEveryProvider()
    {
        var factory = new ChatModelFactory(new SingleClientFactory());
        Assert.True(factory.Create(Settings(ModelProvider.Anthropic), "key-12345678") is AnthropicChatModel, "anthropic");
        Assert.True(factory.Create(Settings(ModelProvider.OpenAiCompatible), "key-12345678") is OpenAiCompatibleChatModel, "openai");
        Assert.True(factory.Create(Settings(ModelProvider.Gemini), "key-12345678") is GeminiChatModel, "gemini");
    }

    private const string GeminiKey = "AIza-gemini-secret-key";

    private static GeminiChatModel Gemini(StubHandler handler, string model = "gemini-2.5-flash") =>
        new(new HttpClient(handler), ModelSettings.Create(ModelProvider.Gemini, model, null, "v1:x:y:z", "abcd", null), GeminiKey);

    public static void GeminiStreamsTextThenFunctionCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        var events = Collect(Gemini(handler), Simple());

        Assert.Equal(4, events.Count);
        Assert.Equal("Let me ", ((TextDelta)events[0]).Text);
        Assert.Equal("look.", ((TextDelta)events[1]).Text);
        var call = (ToolCall)events[2];
        Assert.Equal("find_nodes-0", call.Id);
        Assert.Equal("find_nodes", call.Name);
        Assert.Equal("Orders", call.Args.GetProperty("query").GetString());
        var done = (Finished)events[3];
        Assert.Equal(StopReason.ToolUse, done.Reason);
        Assert.Equal(new Usage(31, 30), done.Usage); // output = candidates (18) + thoughts (12)
    }

    public static void GeminiUnauthorizedMapsToModelAuth()
    {
        AssertFails(Gemini(new StubHandler(HttpStatusCode.Unauthorized, "{}")), "model.auth", GeminiKey);
        AssertFails(Gemini(new StubHandler(HttpStatusCode.Forbidden, "{}")), "model.auth", GeminiKey);
        AssertFails(Gemini(new StubHandler(HttpStatusCode.TooManyRequests, "{}")), "model.rate_limited", GeminiKey);
        AssertFails(Gemini(new StubHandler(HttpStatusCode.ServiceUnavailable, "{}")), "model.unavailable", GeminiKey);
        AssertFails(Gemini(new StubHandler(HttpStatusCode.BadRequest, "{}")), "model.rejected", GeminiKey);
    }

    public static void GeminiKeyTravelsInHeaderNotUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        _ = Collect(Gemini(handler), Simple());

        var url = handler.Request!.RequestUri!.ToString();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", url);
        Assert.True(!url.Contains(GeminiKey, StringComparison.Ordinal), "key not in the URL");
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal(GeminiKey, handler.Request.Headers.GetValues("x-goog-api-key").Single());
        Assert.True(!handler.Request.Headers.Contains("Authorization"), "no bearer header");
        Assert.True(!handler.RequestBody!.Contains(GeminiKey, StringComparison.Ordinal), "key not in the body");
    }

    public static void GeminiRequestCarriesInstructionsAndFunctionDeclarations()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        _ = Collect(Gemini(handler), Simple());
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;

        Assert.Equal("be brief", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var user = root.GetProperty("contents")[0];
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.Equal("hi", user.GetProperty("parts")[0].GetProperty("text").GetString());
        var declaration = root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        Assert.Equal("find_nodes", declaration.GetProperty("name").GetString());
        Assert.Equal("Find nodes.", declaration.GetProperty("description").GetString());
        Assert.Equal("object", declaration.GetProperty("parameters").GetProperty("type").GetString());
        Assert.True(!root.TryGetProperty("model", out _), "the model goes in the URL, not the body");
    }

    public static void GeminiSanitizesSchemaToWhatItAccepts()
    {
        var schema = JsonDocument.Parse("""
            {"$schema":"http://json-schema.org/draft-07/schema#","type":"object","additionalProperties":false,
             "properties":{"kind":{"type":"string","enum":["App","Type"],"description":"Kind."},
                           "limit":{"type":["integer","null"],"minimum":1},
                           "options":{"type":"array","items":{"type":"object","properties":{"key":{"type":"string"}},"required":["key"],"additionalProperties":false}}},
             "required":["kind"]}
            """).RootElement.Clone();
        var empty = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        _ = Collect(Gemini(handler), new ModelRequest("x", [new ChatMessage(ChatRole.User, "hi")],
            [new ToolSpec("draft", "Draft.", schema), new ToolSpec("ping", "Ping.", empty)]));
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var declarations = doc.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations");

        var p = declarations[0].GetProperty("parameters");
        Assert.True(!p.TryGetProperty("$schema", out _), "$schema dropped");
        Assert.True(!p.TryGetProperty("additionalProperties", out _), "additionalProperties dropped");
        Assert.Equal("kind", p.GetProperty("required")[0].GetString());
        Assert.Equal("Type", p.GetProperty("properties").GetProperty("kind").GetProperty("enum")[1].GetString());
        var limit = p.GetProperty("properties").GetProperty("limit");
        Assert.Equal("integer", limit.GetProperty("type").GetString());
        Assert.True(limit.GetProperty("nullable").GetBoolean(), "a null type becomes nullable");
        var item = p.GetProperty("properties").GetProperty("options").GetProperty("items");
        Assert.True(!item.TryGetProperty("additionalProperties", out _), "nested additionalProperties dropped");
        Assert.Equal("string", item.GetProperty("properties").GetProperty("key").GetProperty("type").GetString());
        // Gemini refuses an OBJECT with no properties, so a tool that takes nothing declares no parameters.
        Assert.Equal("ping", declarations[1].GetProperty("name").GetString());
        Assert.True(!declarations[1].TryGetProperty("parameters", out _), "no parameters for an empty object");
    }

    public static void GeminiMapsToolHistoryToFunctionParts()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        _ = Collect(Gemini(handler), WithToolRound());
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var contents = doc.RootElement.GetProperty("contents");

        Assert.Equal(3, contents.GetArrayLength());
        var model = contents[1];
        Assert.Equal("model", model.GetProperty("role").GetString());
        Assert.Equal("On it.", model.GetProperty("parts")[0].GetProperty("text").GetString());
        var call = model.GetProperty("parts")[1].GetProperty("functionCall");
        Assert.Equal("find_nodes", call.GetProperty("name").GetString());
        Assert.Equal("Orders", call.GetProperty("args").GetProperty("query").GetString());
        var results = contents[2];
        Assert.Equal("user", results.GetProperty("role").GetString());
        var response = results.GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("find_nodes", response.GetProperty("name").GetString()); // tied to the call by name
        Assert.Equal(0, response.GetProperty("response").GetProperty("nodes").GetArrayLength());
    }

    public static void GeminiSignsReplayedCallsOnlyForModelsThatNeedIt()
    {
        static JsonElement FirstCall(GeminiChatModel model, StubHandler handler)
        {
            _ = Collect(model, WithToolRound());
            return JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("contents")[1].GetProperty("parts")[1].Clone();
        }

        var old = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        Assert.True(!FirstCall(Gemini(old, "gemini-2.5-flash"), old).TryGetProperty("thoughtSignature", out _), "2.x needs no signature");
        var next = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        Assert.Equal("skip_thought_signature_validator", FirstCall(Gemini(next, "gemini-3-pro-preview"), next).GetProperty("thoughtSignature").GetString());
    }

    public static void GeminiGroupsParallelResultsAndWrapsNonObjects()
    {
        var args = JsonDocument.Parse("{}").RootElement.Clone();
        var request = new ModelRequest("x", [
            new ChatMessage(ChatRole.User, "go"),
            new ChatMessage(ChatRole.Assistant, null, [new ToolCallPart("find_nodes-0", "find_nodes", args), new ToolCallPart("node_detail-1", "node_detail", args)]),
            new ChatMessage(ChatRole.Tool, null, null, new ToolResultPart("find_nodes-0", JsonDocument.Parse("[1,2]").RootElement.Clone(), false)),
            new ChatMessage(ChatRole.Tool, null, null, new ToolResultPart("node_detail-1", JsonDocument.Parse("\"gone\"").RootElement.Clone(), true)),
            new ChatMessage(ChatRole.Assistant, ""),
            new ChatMessage(ChatRole.User, "thanks")], []);
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        _ = Collect(Gemini(handler), request);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var contents = doc.RootElement.GetProperty("contents");

        Assert.Equal(4, contents.GetArrayLength()); // the empty model turn is skipped
        Assert.Equal(2, contents[1].GetProperty("parts").GetArrayLength()); // two functionCall parts, no empty text part
        var parts = contents[2].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal(2, parts[0].GetProperty("functionResponse").GetProperty("response").GetProperty("result").GetArrayLength());
        Assert.Equal("node_detail", parts[1].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal("gone", parts[1].GetProperty("functionResponse").GetProperty("response").GetProperty("error").GetString());
        Assert.True(!doc.RootElement.TryGetProperty("tools", out _), "no tools when none are offered");
    }

    public static void GeminiNumbersParallelCallsAndEndsTurn()
    {
        const string body = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"find_nodes\",\"args\":{\"query\":\"A\"}}},{\"functionCall\":{\"name\":\"find_nodes\"}}],\"role\":\"model\"}}]}\n\n"
            + "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"\"}],\"role\":\"model\"},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":9,\"candidatesTokenCount\":3}}\n\n";
        var events = Collect(Gemini(new StubHandler(HttpStatusCode.OK, body)), Simple());
        var calls = events.OfType<ToolCall>().ToList();
        Assert.Equal("find_nodes-0", calls[0].Id);
        Assert.Equal("find_nodes-1", calls[1].Id);
        Assert.Equal(JsonValueKind.Object, calls[1].Args.ValueKind);
        Assert.Equal(new Usage(9, 3), ((Finished)events[^1]).Usage);
        Assert.Equal(StopReason.ToolUse, ((Finished)events[^1]).Reason);

        const string text = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Done.\"}],\"role\":\"model\"},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":4,\"candidatesTokenCount\":2}}\n\n";
        Assert.Equal(StopReason.EndTurn, ((Finished)Collect(Gemini(new StubHandler(HttpStatusCode.OK, text)), Simple())[^1]).Reason);
        const string cut = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Lo\"}],\"role\":\"model\"},\"finishReason\":\"MAX_TOKENS\"}]}\n\n";
        var max = (Finished)Collect(Gemini(new StubHandler(HttpStatusCode.OK, cut)), Simple())[^1];
        Assert.Equal(StopReason.MaxTokens, max.Reason);
        Assert.Equal(new Usage(0, 0), max.Usage);
    }

    public static void GeminiTruncatedStreamIsUnavailable()
    {
        const string head = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Let me\"}],\"role\":\"model\"}}]}\n\n";
        AssertFails(Gemini(new StubHandler(HttpStatusCode.OK, head)), "model.unavailable", GeminiKey);
        AssertFails(Gemini(new StubHandler(HttpStatusCode.OK, "data: not json\n\n")), "model.unavailable", GeminiKey);
        AssertFails(Gemini(new StubHandler(HttpStatusCode.OK, "data: {\"error\":{\"code\":500,\"message\":\"internal\",\"status\":\"INTERNAL\"}}\n\n")), "model.unavailable", GeminiKey);
        AssertFails(Gemini(new StubHandler(HttpStatusCode.OK, "")), "model.unavailable", GeminiKey);
    }

    public static void GeminiSafetyBlockIsAPlainError()
    {
        const string safety = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"\"}],\"role\":\"model\"},\"finishReason\":\"SAFETY\",\"safetyRatings\":[{\"category\":\"HARM_CATEGORY_DANGEROUS_CONTENT\",\"probability\":\"HIGH\",\"blocked\":true}]}]}\n\n";
        AssertFails(Gemini(new StubHandler(HttpStatusCode.OK, safety)), "model.rejected", GeminiKey);
        const string prompt = "data: {\"promptFeedback\":{\"blockReason\":\"PROHIBITED_CONTENT\"},\"usageMetadata\":{\"promptTokenCount\":5}}\n\n";
        AssertFails(Gemini(new StubHandler(HttpStatusCode.OK, prompt)), "model.rejected", GeminiKey);
    }

    public static void GeminiEncodesAndValidatesTheModelName()
    {
        var prefixed = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
        _ = Collect(Gemini(prefixed, "models/gemini-2.0-flash-001"), Simple());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash-001:streamGenerateContent?alt=sse", prefixed.Request!.RequestUri!.ToString());

        foreach (var bad in new[] { "../../v1/files", "gemini flash", "gemini?alt=json", "gemini#x", "a/b", "gemini:generateContent" })
        {
            var handler = new StubHandler(HttpStatusCode.OK, Fixture("gemini-tool-stream.txt"));
            AssertFails(Gemini(handler, bad), "model.rejected", GeminiKey);
            Assert.True(handler.Request is null, "no request is sent for a bad name: " + bad);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
