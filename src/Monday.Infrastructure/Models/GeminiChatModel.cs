using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monday.Application.Abstractions;
using Monday.Domain.Agent;

namespace Monday.Infrastructure.Models;

/// <summary>Google's Gemini API (streamGenerateContent as server-sent events) over plain HttpClient.</summary>
public sealed class GeminiChatModel(HttpClient http, ModelSettings settings, string apiKey, TimeSpan? idleTimeout = null) : IChatModel
{
    internal TimeSpan IdleTimeout { get; } = idleTimeout ?? ModelHttp.DefaultIdleTimeout;

    /// <summary>Fixed: Gemini has no configurable base URL. The "models" client still dials it through SafeEndpoint.</summary>
    private const string ModelsBase = "https://generativelanguage.googleapis.com/v1beta/models/";

    /// <summary>
    /// Gemini 3 and later refuse a replayed functionCall without the thought signature it came with. Monday keeps no
    /// signatures, so it sends the placeholder Google documents for calls it cannot sign. Gemini 1.x and 2.x need none.
    /// </summary>
    private const string PlaceholderSignature = "skip_thought_signature_validator";

    /// <summary>
    /// The JSON Schema keywords Gemini's function parameters accept (its OpenAPI subset). Everything else is dropped,
    /// notably <c>$schema</c>, <c>additionalProperties</c>, <c>$ref</c>/<c>$defs</c>, <c>oneOf</c>/<c>allOf</c>/<c>not</c>
    /// and <c>examples</c>; <c>const</c> becomes a one-value <c>enum</c> and a <c>["x","null"]</c> type becomes <c>nullable</c>.
    /// </summary>
    private static readonly HashSet<string> SchemaKeywords = new(StringComparer.Ordinal)
    {
        "type", "format", "title", "description", "nullable", "enum", "items", "properties", "required",
        "minItems", "maxItems", "minProperties", "maxProperties", "minimum", "maximum", "minLength", "maxLength",
        "pattern", "anyOf", "propertyOrdering", "default",
    };

    /// <summary>Finish reasons that mean Gemini blocked the answer rather than ending it.</summary>
    private static readonly HashSet<string> BlockedReasons = new(StringComparer.Ordinal)
    {
        "SAFETY", "RECITATION", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "IMAGE_SAFETY", "IMAGE_PROHIBITED_CONTENT", "IMAGE_RECITATION",
    };

    public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ModelHttp.GuardAsync(RunAsync(request, ct), ct);
    }

    internal JsonObject BuildBody(ModelRequest request)
    {
        var body = new JsonObject { ["contents"] = BuildContents(request.Messages, NeedsSignatures(settings.Model)) };
        if (!string.IsNullOrEmpty(request.Instructions))
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = request.Instructions } } };
        }

        if (request.Tools.Count > 0)
        {
            var declarations = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var declaration = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
                if (Parameters(tool.InputSchema) is { } parameters)
                {
                    declaration["parameters"] = parameters;
                }

                declarations.Add(declaration);
            }

            body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = declarations } };
        }

        return body;
    }

    private static JsonArray BuildContents(IReadOnlyList<ChatMessage> history, bool signCalls)
    {
        var contents = new JsonArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonArray? pendingResults = null;
        foreach (var message in history)
        {
            if (message.Role == ChatRole.Tool && message.ToolResult is { } result)
            {
                // All results for one model turn travel together in a single user turn, tied to their calls by name.
                if (pendingResults is null)
                {
                    pendingResults = [];
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = pendingResults });
                }

                pendingResults.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject { ["name"] = NameOf(result.CallId, names), ["response"] = ResponseOf(result) },
                });
                continue;
            }

            pendingResults = null;
            if (message.Role == ChatRole.Assistant)
            {
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(message.Text))
                {
                    parts.Add(new JsonObject { ["text"] = message.Text });
                }

                var first = true;
                foreach (var call in message.ToolCalls ?? [])
                {
                    names[call.Id] = call.Name;
                    var part = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = ModelHttp.ArgsNode(call.Args) } };
                    if (signCalls && first)
                    {
                        part["thoughtSignature"] = PlaceholderSignature; // Gemini signs only the first call of a step
                    }

                    first = false;
                    parts.Add(part);
                }

                if (parts.Count == 0)
                {
                    continue; // Gemini rejects a turn with no parts
                }

                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
            else
            {
                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = message.Text ?? string.Empty } } });
            }
        }

        return contents;
    }

    /// <summary>The tool name for a result: from the call it answers, or else from the "{name}-{index}" id.</summary>
    private static string NameOf(string callId, Dictionary<string, string> names)
    {
        if (names.TryGetValue(callId, out var name))
        {
            return name;
        }

        var dash = callId.LastIndexOf('-');
        var numbered = dash > 0 && dash < callId.Length - 1 && callId.AsSpan(dash + 1).IndexOfAnyExceptInRange('0', '9') < 0;
        return numbered ? callId[..dash] : callId;
    }

    /// <summary>Gemini wants an object back; anything else is wrapped as {"result": …}, or {"error": …} for a failed call.</summary>
    private static JsonNode ResponseOf(ToolResultPart result) =>
        result.Content.ValueKind == JsonValueKind.Object
            ? ModelHttp.ArgsNode(result.Content)
            : new JsonObject { [result.IsError ? "error" : "result"] = JsonNode.Parse(ModelHttp.RawText(result.Content)) };

    /// <summary>The tool's schema in Gemini's subset, or null for a tool that takes nothing (Gemini refuses an OBJECT with no properties).</summary>
    internal static JsonObject? Parameters(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var clean = Sanitize(schema);
        return clean.ContainsKey("properties") || (clean["type"]?.GetValue<string>() is { } type && type != "object") ? clean : null;
    }

    private static JsonObject Sanitize(JsonElement schema)
    {
        var clean = new JsonObject();
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return clean;
        }

        foreach (var property in schema.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "type" when value.ValueKind == JsonValueKind.Array:
                    var types = value.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList();
                    if (types.FirstOrDefault(t => t != "null") is { } single)
                    {
                        clean["type"] = single;
                    }

                    if (types.Contains("null"))
                    {
                        clean["nullable"] = true;
                    }

                    break;
                case "properties" when value.ValueKind == JsonValueKind.Object:
                    var properties = new JsonObject();
                    foreach (var p in value.EnumerateObject())
                    {
                        properties[p.Name] = Sanitize(p.Value);
                    }

                    if (properties.Count > 0)
                    {
                        clean["properties"] = properties;
                    }

                    break;
                case "items":
                    clean["items"] = Sanitize(value);
                    break;
                case "anyOf" when value.ValueKind == JsonValueKind.Array:
                    clean["anyOf"] = new JsonArray([.. value.EnumerateArray().Select(JsonNode (v) => Sanitize(v))]);
                    break;
                case "const" when value.ValueKind == JsonValueKind.String && !schema.TryGetProperty("enum", out _):
                    clean["enum"] = new JsonArray { value.GetString() };
                    break;
                case var name when SchemaKeywords.Contains(name) && name is not ("properties" or "anyOf"):
                    clean[name] = JsonNode.Parse(value.GetRawText());
                    break;
                default:
                    break; // not in Gemini's subset: dropped
            }
        }

        return clean;
    }

    /// <summary>The model's URL segment. Only letters, digits, '.', '-' and '_' (after an optional "models/"), so a name can't reach another path.</summary>
    internal static string ModelSegment(string model)
    {
        var name = model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;
        var valid = name.Length > 0 && char.IsAsciiLetterOrDigit(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
        return valid
            ? Uri.EscapeDataString(name)
            : throw new ModelException("model.rejected", "A Gemini model name can only hold letters, digits, '.', '-' and '_'.");
    }

    private static bool NeedsSignatures(string model)
    {
        var name = model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;
        return !(name.StartsWith("gemini-1.", StringComparison.Ordinal) || name.StartsWith("gemini-2.", StringComparison.Ordinal));
    }

    private async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken callerCt)
    {
        var endpoint = new Uri(ModelsBase + ModelSegment(settings.Model) + ":streamGenerateContent?alt=sse");

        // Gives up when nothing arrives for IdleTimeout; every line read resets it. GuardAsync turns that into model.unavailable.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        idle.CancelAfter(IdleTimeout);
        var ct = idle.Token;
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = ModelHttp.Json(BuildBody(request)) };
        message.Headers.Add("x-goog-api-key", apiKey); // the key goes in a header, never the URL
        using var response = await ModelHttp.SendAsync(http, message, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var calls = 0;
        string? finishReason = null;
        var usage = new Usage(0, 0);

        await foreach (var (_, data) in SseReader.ReadAsync(stream, ct, () => idle.CancelAfter(IdleTimeout)).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                throw new ModelException("model.unavailable", "The model provider reported an error mid-stream.");
            }

            if (root.TryGetProperty("usageMetadata", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                // Each chunk carries the running totals. Thinking tokens are billed as output.
                usage = new Usage(Int(u, "promptTokenCount"), Int(u, "candidatesTokenCount") + Int(u, "thoughtsTokenCount"));
            }

            if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object
                && feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String)
            {
                throw new ModelException("model.rejected", "The model provider blocked this request for safety or policy reasons.");
            }

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            {
                continue;
            }

            var candidate = candidates[0];
            if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                    {
                        continue; // a thought summary, not answer text
                    }

                    if (part.TryGetProperty("functionCall", out var call) && call.ValueKind == JsonValueKind.Object)
                    {
                        var name = call.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
                        var args = !call.TryGetProperty("args", out var a) ? ModelHttp.ParseArgs("{}")
                            : a.ValueKind == JsonValueKind.Object ? a.Clone() : ModelHttp.ParseArgs(a.GetRawText());
                        yield return new ToolCall($"{name}-{calls++}", name, args); // Gemini has no call id
                    }
                    else if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } t)
                    {
                        yield return new TextDelta(t);
                    }
                }
            }

            if (candidate.TryGetProperty("finishReason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString();
                if (finishReason is not null && BlockedReasons.Contains(finishReason))
                {
                    throw new ModelException("model.rejected", "The model provider blocked this answer for safety or policy reasons.");
                }
            }
        }

        if (finishReason is null)
        {
            throw new ModelException("model.unavailable", "The model provider's stream ended early.");
        }

        yield return new Finished(MapReason(finishReason, calls > 0), usage);
    }

    /// <summary>Gemini says STOP even after a function call, so a turn that called tools is ToolUse.</summary>
    private static StopReason MapReason(string reason, bool calledTools) => reason switch
    {
        "STOP" => calledTools ? StopReason.ToolUse : StopReason.EndTurn,
        "MAX_TOKENS" => StopReason.MaxTokens,
        _ => StopReason.Error,
    };

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
