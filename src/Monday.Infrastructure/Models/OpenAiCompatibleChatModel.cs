using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monday.Application.Abstractions;
using Monday.Domain.Agent;

namespace Monday.Infrastructure.Models;

/// <summary>Any OpenAI-style chat completions endpoint over plain HttpClient, streamed.</summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, ModelSettings settings, string apiKey, TimeSpan? idleTimeout = null) : IChatModel
{
    internal TimeSpan IdleTimeout { get; } = idleTimeout ?? ModelHttp.DefaultIdleTimeout;

    private const string DefaultBase = "https://api.openai.com/v1";

    public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ModelHttp.GuardAsync(RunAsync(request, ct), ct);
    }

    internal JsonObject BuildBody(ModelRequest request)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.Instructions } };
        foreach (var message in request.Messages)
        {
            if (message.Role == ChatRole.Tool && message.ToolResult is { } result)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.CallId,
                    ["content"] = ModelHttp.RawText(result.Content),
                });
            }
            else if (message.Role == ChatRole.Assistant)
            {
                var node = new JsonObject { ["role"] = "assistant", ["content"] = string.IsNullOrEmpty(message.Text) ? null : message.Text };
                if (message.ToolCalls is { Count: > 0 } calls)
                {
                    var array = new JsonArray();
                    foreach (var call in calls)
                    {
                        array.Add(new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = ModelHttp.RawText(call.Args) },
                        });
                    }

                    node["tool_calls"] = array;
                }

                messages.Add(node);
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text ?? string.Empty });
            }
        }

        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonSerializer.SerializeToNode(tool.InputSchema),
                    },
                });
            }

            body["tools"] = tools;
        }

        return body;
    }

    private Uri EndpointUri() =>
        new((settings.BaseUrl?.AbsoluteUri ?? DefaultBase).TrimEnd('/') + "/chat/completions");

    private async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken callerCt)
    {
        // Gives up when nothing arrives for IdleTimeout; every line read resets it. GuardAsync turns that into model.unavailable.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        idle.CancelAfter(IdleTimeout);
        var ct = idle.Token;
        using var message = new HttpRequestMessage(HttpMethod.Post, EndpointUri()) { Content = ModelHttp.Json(BuildBody(request)) };
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        using var response = await ModelHttp.SendAsync(http, message, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? finishReason = null;
        var usage = new Usage(0, 0);

        await foreach (var (_, data) in SseReader.ReadAsync(stream, ct, () => idle.CancelAfter(IdleTimeout)).ConfigureAwait(false))
        {
            if (data == "[DONE]")
            {
                break;
            }

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                throw new ModelException("model.unavailable", "The model provider reported an error mid-stream.");
            }

            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new Usage(Int(u, "prompt_tokens"), Int(u, "completion_tokens"));
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                {
                    yield return new TextDelta(text);
                }

                if (delta.TryGetProperty("tool_calls", out var fragments) && fragments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fragment in fragments.EnumerateArray())
                    {
                        Accumulate(calls, fragment);
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString();
                foreach (var (id, name, args) in calls.Values)
                {
                    yield return new ToolCall(string.IsNullOrEmpty(id) ? "call_" + Guid.NewGuid().ToString("N") : id, name, ModelHttp.ParseArgs(args.ToString()));
                }

                var hadCalls = calls.Count > 0;
                calls.Clear();
                if (hadCalls)
                {
                    finishReason = "tool_calls";
                }
            }
        }

        if (finishReason is null)
        {
            throw new ModelException("model.unavailable", "The model provider's stream ended early.");
        }

        yield return new Finished(MapReason(finishReason), usage);
    }

    private static void Accumulate(SortedDictionary<int, (string Id, string Name, StringBuilder Args)> calls, JsonElement fragment)
    {
        var index = fragment.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
        if (!calls.TryGetValue(index, out var call))
        {
            call = (string.Empty, string.Empty, new StringBuilder());
        }

        var id = call.Id;
        var name = call.Name;
        if (fragment.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            id = idElement.GetString() ?? id;
        }

        if (fragment.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (function.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                name = n.GetString() ?? name;
            }

            if (function.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
            {
                call.Args.Append(a.GetString());
            }
        }

        calls[index] = (id, name, call.Args);
    }

    private static StopReason MapReason(string? reason) => reason switch
    {
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        "stop" or null => StopReason.EndTurn,
        _ => StopReason.Error,
    };

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
