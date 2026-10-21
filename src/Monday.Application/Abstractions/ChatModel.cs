using System.Text.Json;
using Monday.Application.Agent;
using Monday.Domain.Agent;

namespace Monday.Application.Abstractions;

/// <summary>Who wrote a chat message. Tool messages carry a tool result back to the model.</summary>
public enum ChatRole
{
    User,
    Assistant,
    Tool,
}

/// <summary>A tool the model asked to run.</summary>
public sealed record ToolCallPart(string Id, string Name, JsonElement Args);

/// <summary>What a tool returned for an earlier call.</summary>
public sealed record ToolResultPart(string CallId, JsonElement Content, bool IsError);

/// <summary>One message in the conversation, as any model provider needs to see it.</summary>
public sealed record ChatMessage(
    ChatRole Role,
    string? Text,
    IReadOnlyList<ToolCallPart>? ToolCalls = null,
    ToolResultPart? ToolResult = null);

/// <summary>Everything one model call needs: instructions, the conversation so far, and the tools on offer.</summary>
public sealed record ModelRequest(string Instructions, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolSpec> Tools);

/// <summary>Why the model stopped.</summary>
public enum StopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    Error,
}

/// <summary>Tokens used by one model call.</summary>
public sealed record Usage(int InputTokens, int OutputTokens);

/// <summary>One thing a model streams back.</summary>
public abstract record ModelEvent;

/// <summary>A piece of the answer text.</summary>
public sealed record TextDelta(string Text) : ModelEvent;

/// <summary>The model asks for a tool to run.</summary>
public sealed record ToolCall(string Id, string Name, JsonElement Args) : ModelEvent;

/// <summary>The model is done with this turn.</summary>
public sealed record Finished(StopReason Reason, Usage Usage) : ModelEvent;

/// <summary>A model provider, seen through one neutral interface. Adapters implement it over HttpClient.</summary>
public interface IChatModel
{
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct);
}

/// <summary>Builds the adapter for a project's model settings, with the key already opened. (Named Create, not For: CA1716.)</summary>
public interface IChatModelFactory
{
    IChatModel Create(ModelSettings settings, string apiKey);
}
