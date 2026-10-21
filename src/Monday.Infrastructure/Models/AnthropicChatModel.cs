using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monday.Application.Abstractions;
using Monday.Domain.Agent;

namespace Monday.Infrastructure.Models;

/// <summary>Anthropic's Messages API over plain HttpClient, streamed.</summary>
public sealed class AnthropicChatModel(HttpClient http, ModelSettings settings, string apiKey, TimeSpan? idleTimeout = null) : IChatModel
{
    internal TimeSpan IdleTimeout { get; } = idleTimeout ?? ModelHttp.DefaultIdleTimeout;

    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const int MaxTokens = 4096;

    public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ModelHttp.GuardAsync(RunAsync(request, ct), ct);
    }

    internal JsonObject BuildBody(ModelRequest request)
    {
        var tools = new JsonArray();
        foreach (var tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonSerializer.SerializeToNode(tool.InputSchema),
            });
        }

        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["system"] = request.Instructions,
            ["messages"] = BuildMessages(request.Messages),
            ["max_tokens"] = MaxTokens,
            ["stream"] = true,
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        return body;
    }

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> history)
    {
        var messages = new JsonArray();
        JsonArray? pendingResults = null;
        foreach (var message in history)
        {
            if (message.Role == ChatRole.Tool && message.ToolResult is { } result)
            {
                // All results for one assistant turn travel together in a single user turn.
                if (pendingResults is null)
                {
                    pendingResults = [];
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = pendingResults });
                }

                var block = new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = result.CallId,
                    ["content"] = ModelHttp.RawText(result.Content),
                };
                if (result.IsError)
                {
                    block["is_error"] = true;
                }

                pendingResults.Add(block);
                continue;
            }

            pendingResults = null;
            if (message.Role == ChatRole.Assistant)
            {
                if (string.IsNullOrEmpty(message.Text) && message.ToolCalls is not { Count: > 0 })
                {
                    continue; // Anthropic rejects an assistant turn with no content
                }

                var content = new JsonArray();
                if (!string.IsNullOrEmpty(message.Text))
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });
                }

                foreach (var call in message.ToolCalls ?? [])
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = ModelHttp.ArgsNode(call.Args),
                    });
                }

                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text ?? string.Empty });
            }
        }

        return messages;
    }

    private async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken callerCt)
    {
        // Gives up when nothing arrives for IdleTimeout; every line read resets it. GuardAsync turns that into model.unavailable.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        idle.CancelAfter(IdleTimeout);
        var ct = idle.Token;
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = ModelHttp.Json(BuildBody(request)) };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await ModelHttp.SendAsync(http, message, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var toolBlocks = new Dictionary<int, (string Id, string Name, StringBuilder Json)>();
        var inputTokens = 0;
        var outputTokens = 0;
        var reason = StopReason.EndTurn;
        var finished = false;

        await foreach (var (_, data) in SseReader.ReadAsync(stream, ct, () => idle.CancelAfter(IdleTimeout)).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var startUsage))
                    {
                        inputTokens = Int(startUsage, "input_tokens", inputTokens);
                        outputTokens = Int(startUsage, "output_tokens", outputTokens);
                    }

                    break;
                case "content_block_start":
                    if (root.TryGetProperty("content_block", out var block) && Str(block, "type") == "tool_use")
                    {
                        toolBlocks[root.GetProperty("index").GetInt32()] = (Str(block, "id") ?? string.Empty, Str(block, "name") ?? string.Empty, new StringBuilder());
                    }

                    break;
                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        if (Str(delta, "type") == "text_delta" && Str(delta, "text") is { Length: > 0 } text)
                        {
                            yield return new TextDelta(text);
                        }
                        else if (Str(delta, "type") == "input_json_delta" && toolBlocks.TryGetValue(root.GetProperty("index").GetInt32(), out var open))
                        {
                            open.Json.Append(Str(delta, "partial_json"));
                        }
                    }

                    break;
                case "content_block_stop":
                    if (toolBlocks.Remove(root.GetProperty("index").GetInt32(), out var done))
                    {
                        yield return new ToolCall(done.Id, done.Name, ModelHttp.ParseArgs(done.Json.ToString()));
                    }

                    break;
                case "message_delta":
                    if (root.TryGetProperty("delta", out var md))
                    {
                        reason = MapReason(Str(md, "stop_reason"));
                    }

                    if (root.TryGetProperty("usage", out var u))
                    {
                        inputTokens = Int(u, "input_tokens", inputTokens);
                        outputTokens = Int(u, "output_tokens", outputTokens);
                    }

                    break;
                case "message_stop":
                    finished = true;
                    yield return new Finished(reason, new Usage(inputTokens, outputTokens));
                    break;
                case "error":
                    throw new ModelException("model.unavailable", "The model provider reported an error mid-stream.");
                default:
                    break;
            }
        }

        if (!finished)
        {
            throw new ModelException("model.unavailable", "The model provider's stream ended early.");
        }
    }

    private static StopReason MapReason(string? reason) => reason switch
    {
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "end_turn" or "stop_sequence" or null => StopReason.EndTurn,
        _ => StopReason.Error,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}
