using System.Text.Json;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Agent;

public readonly record struct ConversationId(Guid Value)
{
    public static ConversationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>Who wrote a message in a conversation. Tool messages carry a tool result back to the model.</summary>
public enum ConversationRole
{
    User,
    Assistant,
    Tool,
}

/// <summary>A tool the agent asked to run.</summary>
public sealed record ConversationToolCall(string Id, string Name, JsonElement Args);

/// <summary>What a tool returned for an earlier call. Never a key or secret: tools only return what the API shows.</summary>
public sealed record ConversationToolResult(string CallId, JsonElement Content, bool IsError);

/// <summary>One message in a stored conversation.</summary>
public sealed record ConversationMessage
{
    private ConversationMessage(ConversationRole role, string? text, IReadOnlyList<ConversationToolCall> toolCalls, ConversationToolResult? toolResult, DateTimeOffset at)
    {
        Role = role;
        Text = text;
        ToolCalls = toolCalls;
        ToolResult = toolResult;
        At = at;
    }

    public ConversationRole Role { get; }

    public string? Text { get; }

    public IReadOnlyList<ConversationToolCall> ToolCalls { get; }

    public ConversationToolResult? ToolResult { get; }

    public DateTimeOffset At { get; }

    /// <summary>What the person asked: not blank, at most <see cref="Conversation.MaxMessageLength"/> characters.</summary>
    public static ConversationMessage User(string text, DateTimeOffset at)
    {
        Guard.That(!string.IsNullOrWhiteSpace(text), "conversation.message_required", "A message is required.");
        var trimmed = text.Trim();
        Guard.That(trimmed.Length <= Conversation.MaxMessageLength, "conversation.message_too_long",
            $"A message has at most {Conversation.MaxMessageLength} characters.");
        return new ConversationMessage(ConversationRole.User, trimmed, [], null, at);
    }

    public static ConversationMessage Assistant(string? text, IReadOnlyList<ConversationToolCall>? toolCalls, DateTimeOffset at) =>
        new(ConversationRole.Assistant, string.IsNullOrEmpty(text) ? null : text, toolCalls ?? [], null, at);

    public static ConversationMessage Tool(ConversationToolResult result, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ConversationMessage(ConversationRole.Tool, null, [], result, at);
    }

    /// <summary>Rebuilds a stored message (persistence only).</summary>
    public static ConversationMessage Restore(ConversationRole role, string? text, IReadOnlyList<ConversationToolCall>? toolCalls, ConversationToolResult? toolResult, DateTimeOffset at) =>
        new(role, text, toolCalls ?? [], toolResult, at);
}

/// <summary>
/// One person's chat with the agent on one project. It is theirs alone: no one else on the project, not even an
/// Owner, can read it. Messages are only ever added.
/// </summary>
public sealed class Conversation : AggregateRoot<ConversationId>
{
    public const int MaxMessageLength = 4000;
    public const int MaxTitleLength = 80;

    private readonly List<ConversationMessage> _messages = [];

    private Conversation(ConversationId id, ProjectId projectId, UserId owner, string title, DateTimeOffset createdAt)
        : base(id)
    {
        ProjectId = projectId;
        Owner = owner;
        Title = title;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public ProjectId ProjectId { get; }

    public UserId Owner { get; }

    /// <summary>The first message on one line, cut to <see cref="MaxTitleLength"/> characters.</summary>
    public string Title { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ConversationMessage> Messages => _messages;

    /// <summary>How many messages the store held when this copy was loaded or last saved. A save from a stale copy is refused.</summary>
    public int StoredCount { get; private set; }

    public bool HasUnsaved => _messages.Count > StoredCount;

    /// <summary>Persistence only: the store calls it after a save.</summary>
    public void MarkStored() => StoredCount = _messages.Count;

    public static Conversation Start(ProjectId projectId, UserId owner, string firstMessage, DateTimeOffset at)
    {
        var first = ConversationMessage.User(firstMessage, at);
        var conversation = new Conversation(ConversationId.New(), projectId, owner, TitleOf(first.Text!), at);
        conversation.Append(first);
        return conversation;
    }

    /// <summary>Rebuilds a stored conversation (persistence only).</summary>
    public static Conversation Restore(ConversationId id, ProjectId projectId, UserId owner, string title, DateTimeOffset createdAt, IEnumerable<ConversationMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var conversation = new Conversation(id, projectId, owner, title, createdAt);
        foreach (var message in messages)
        {
            conversation.Add(message);
        }

        conversation.MarkStored();
        return conversation;
    }

    public void Append(ConversationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role == ConversationRole.User)
        {
            // Re-checks the rules, so a message built through Restore cannot slip past them.
            ConversationMessage.User(message.Text ?? string.Empty, message.At);
        }

        Add(message);
    }

    public bool IsVisibleTo(UserId user) => user == Owner;

    private void Add(ConversationMessage message)
    {
        _messages.Add(message);
        if (message.At > UpdatedAt)
        {
            UpdatedAt = message.At;
        }
    }

    private static string TitleOf(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxTitleLength ? line : string.Concat(line.AsSpan(0, MaxTitleLength - 1), "…");
    }
}
