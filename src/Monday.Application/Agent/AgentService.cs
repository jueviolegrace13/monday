using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Agent;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Agent;

/// <summary>What the person sends: a message, optionally in an earlier conversation, with the node they have selected.</summary>
public sealed record ChatInput(Guid? ConversationId, string Message, string? SelectedNodeId);

/// <summary>One thing the agent streams to the viewer while it answers.</summary>
public abstract record AgentEvent;

/// <summary>A piece of the answer text.</summary>
public sealed record AgentText(string Text) : AgentEvent;

/// <summary>A tool ran (or failed), with the graph nodes it touched.</summary>
public sealed record AgentTool(string Name, IReadOnlyList<string> NodeIds, bool IsError) : AgentEvent;

/// <summary>The viewer should change the screen.</summary>
public sealed record AgentAction(ScreenAction Action) : AgentEvent;

/// <summary>The answer is complete.</summary>
public sealed record AgentDone(Guid ConversationId, Usage Usage) : AgentEvent;

/// <summary>The turn ended without an answer, for a reason the person can read.</summary>
public sealed record AgentError(string Code, string Message) : AgentEvent;

/// <summary>A message as the history screen shows it: the words, and the names of the tools the agent used.</summary>
public sealed record ConversationMessageView(ConversationRole Role, string? Text, IReadOnlyList<string> Tools, DateTimeOffset At);

public sealed record ConversationView(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ConversationMessageView> Messages);

/// <summary>
/// The agent loop. It talks to the project's model, runs the tools the model asks for as the signed-in member (in
/// their real role, so the services' permission checks apply), keeps each person's history, and records token use.
/// </summary>
public sealed class AgentService(
    IProjectRepository projects,
    IConversationStore conversations,
    IModelUsageStore usage,
    ISecretBox secrets,
    IChatModelFactory models,
    ToolRegistry tools,
    IClock clock)
{
    /// <summary>Most model turns that run tools for one message; then the loop stops and says so.</summary>
    public const int MaxToolSteps = 8;

    private const int MaxSelectedNodeLength = 512;

    public async IAsyncEnumerable<AgentEvent> ChatAsync(ProjectId id, UserId user, ChatInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Refusals that are errors of the request itself throw, before anything streams, like every other endpoint.
        var (project, role) = await LoadForChatAsync(id, user, ct).ConfigureAwait(false);
        var selected = SelectedNode(input.SelectedNodeId);
        var asked = ConversationMessage.User(input.Message, clock.Now);
        var earlier = input.ConversationId is { } cid ? await LoadOwnAsync(id, cid, user, ct).ConfigureAwait(false) : null;

        // Refusals about the project's model are events, so the chat panel can show them in place.
        var refusal = await RefusalAsync(project, ct).ConfigureAwait(false);
        var (model, failure) = refusal is null ? OpenModel(project.Model!) : (null, refusal);
        if (model is null)
        {
            yield return failure!;
            yield break;
        }

        var conversation = earlier ?? Conversation.Start(id, user, input.Message, asked.At);
        if (earlier is not null)
        {
            conversation.Append(asked);
        }

        var history = conversation.Messages.Select(ToChat).ToList();
        var instructions = AgentInstructions.Build(project, role, selected);
        var offered = tools.For(role, ToolChannel.Chat).Select(t => t.ToSpec()).ToList();
        var context = new ToolContext(id, user, role, ToolChannel.Chat);
        var month = ModelSettingsService.MonthOf(clock.Now);
        var total = new Usage(0, 0);
        var turn = new List<ConversationMessage>();

        try
        {
            for (var steps = 0; ; steps++)
            {
                if (steps == MaxToolSteps)
                {
                    yield return new AgentError("agent.step_cap", $"Stopped after {MaxToolSteps} tool steps. Ask again to carry on from here.");
                    yield break;
                }

                ct.ThrowIfCancellationRequested();
                var text = new StringBuilder();
                var calls = new List<ConversationToolCall>();
                Usage? used = null;
                AgentError? failed = null;
                var stream = model.StreamAsync(new ModelRequest(instructions, [.. history], offered), ct).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        var (more, error) = await NextAsync(stream).ConfigureAwait(false);
                        if (error is not null || !more)
                        {
                            failed = error;
                            break;
                        }

                        switch (stream.Current)
                        {
                            case TextDelta { Text.Length: > 0 } delta:
                                text.Append(delta.Text);
                                yield return new AgentText(delta.Text);
                                break;
                            case ToolCall call:
                                calls.Add(new ConversationToolCall(call.Id, call.Name, call.Args));
                                break;
                            case Finished finished:
                                used = finished.Usage;
                                break;
                        }
                    }
                }
                finally
                {
                    await stream.DisposeAsync().ConfigureAwait(false);

                    // Recorded even when the viewer left mid-turn: the provider charged for it.
                    if (used is not null)
                    {
                        total = new Usage(total.InputTokens + used.InputTokens, total.OutputTokens + used.OutputTokens);
                        await usage.AddAsync(id, month, used, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                if (failed is not null)
                {
                    if (text.Length > 0)
                    {
                        conversation.Append(ConversationMessage.Assistant(text.ToString(), null, clock.Now));
                    }

                    yield return failed;
                    yield break;
                }

                turn.Add(ConversationMessage.Assistant(text.ToString(), calls, clock.Now));
                foreach (var call in calls)
                {
                    var (result, nodeIds, action) = await RunToolAsync(call, context, ct).ConfigureAwait(false);
                    turn.Add(ConversationMessage.Tool(result, clock.Now));
                    yield return new AgentTool(call.Name, nodeIds, result.IsError);
                    if (action is not null)
                    {
                        yield return new AgentAction(action);
                    }
                }

                history.AddRange(turn.Select(ToChat));
                CloseTurn(conversation, turn);
                await conversations.SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);

                if (calls.Count == 0)
                {
                    yield return new AgentDone(conversation.Id.Value, total);
                    yield break;
                }
            }
        }
        finally
        {
            // A turn cut off while its tools ran keeps what finished; each unanswered call gets a "cancelled" result,
            // so the stored history stays valid for providers that require a result for every call.
            CloseTurn(conversation, turn);
            await conversations.SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(ProjectId id, UserId user, CancellationToken ct)
    {
        await LoadForChatAsync(id, user, ct).ConfigureAwait(false);
        return await conversations.ListAsync(id, user, ct).ConfigureAwait(false);
    }

    public async Task<ConversationView> GetConversationAsync(ProjectId id, Guid conversationId, UserId user, CancellationToken ct)
    {
        await LoadForChatAsync(id, user, ct).ConfigureAwait(false);
        var c = await LoadOwnAsync(id, conversationId, user, ct).ConfigureAwait(false);

        // Tool results stay out of the view: the person sees words and tool names, never raw tool data.
        var messages = c.Messages
            .Where(m => m.Role != ConversationRole.Tool)
            .Select(m => new ConversationMessageView(m.Role, m.Text, [.. m.ToolCalls.Select(t => t.Name)], m.At))
            .ToList();
        return new ConversationView(c.Id.Value, c.Title, c.CreatedAt, c.UpdatedAt, messages);
    }

    /// <summary>Non-members get "not found"; members without ChatAsk are refused. The role is the member's real one.</summary>
    private async Task<(Project Project, Role Role)> LoadForChatAsync(ProjectId id, UserId user, CancellationToken ct)
    {
        var project = await projects.GetAsync(id, ct).ConfigureAwait(false);
        if (project?.RoleOf(user) is not { } role)
        {
            throw new NotFoundException("project");
        }

        project.EnsureCan(user, Permission.ChatAsk);
        return (project, role);
    }

    /// <summary>Someone else's conversation, or one from another project, does not exist as far as the asker can tell.</summary>
    private async Task<Conversation> LoadOwnAsync(ProjectId id, Guid conversationId, UserId user, CancellationToken ct)
    {
        var conversation = await conversations.GetAsync(new ConversationId(conversationId), ct).ConfigureAwait(false);
        return conversation is not null && conversation.ProjectId == id && conversation.IsVisibleTo(user)
            ? conversation
            : throw new NotFoundException("conversation");
    }

    private async Task<AgentError?> RefusalAsync(Project project, CancellationToken ct)
    {
        if (project.Model is not { } settings)
        {
            return new AgentError("model.not_configured", "No model is set up for this project yet. An Owner or Admin can add one in settings.");
        }

        if (!secrets.Enabled)
        {
            return new AgentError("model.keys_disabled", "Chat is off: Monday has no MONDAY_SECRETS_KEY to open the model key.");
        }

        if (settings.MonthlyTokenCap is { } cap)
        {
            var used = await usage.MonthAsync(project.Id, ModelSettingsService.MonthOf(clock.Now), ct).ConfigureAwait(false);
            if ((long)used.InputTokens + used.OutputTokens >= cap)
            {
                return new AgentError("model.cap_reached", "This project has used its model tokens for the month.");
            }
        }

        return null;
    }

    private (IChatModel? Model, AgentError? Error) OpenModel(ModelSettings settings)
    {
        try
        {
            return (models.Create(settings, secrets.Open(settings.KeyCipher)), null);
        }
        catch (CryptographicException)
        {
            return (null, new AgentError("model.key_unreadable", "The saved model key can't be opened. An Owner or Admin needs to enter it again."));
        }
        catch (ModelException e)
        {
            return (null, new AgentError(e.Code, e.Message));
        }
    }

    private static string? SelectedNode(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        var id = nodeId.Trim();
        Guard.That(id.Length <= MaxSelectedNodeLength && !id.Any(c => char.IsControl(c) || c == '`'), "agent.input", "The selected node id is not valid.");
        return id;
    }

    /// <summary>The next model event, or the model's failure as an error event (no retries).</summary>
    private static async Task<(bool More, AgentError? Error)> NextAsync(IAsyncEnumerator<ModelEvent> stream)
    {
        try
        {
            return (await stream.MoveNextAsync().ConfigureAwait(false), null);
        }
        catch (ModelException e)
        {
            return (false, new AgentError(e.Code, e.Message));
        }
        catch (DomainException e)
        {
            // For example a base URL that now resolves inside Monday's network.
            return (false, new AgentError(e.Code, e.Message));
        }
    }

    /// <summary>Runs one call as the asker. Never throws for a bad call: the model gets a short error instead.</summary>
    private async Task<(ConversationToolResult Result, IReadOnlyList<string> NodeIds, ScreenAction? Action)> RunToolAsync(
        ConversationToolCall call, ToolContext context, CancellationToken ct)
    {
        var tool = tools.Find(call.Name, context.Role, ToolChannel.Chat);
        if (tool is null)
        {
            return Failed(call, "agent.unknown_tool: No such tool is available.");
        }

        if (call.Args.ValueKind == JsonValueKind.Object && call.Args.TryGetProperty("_invalid", out _))
        {
            return Failed(call, "agent.input: The arguments were not valid JSON.");
        }

        var args = call.Args.ValueKind == JsonValueKind.Object ? call.Args : EmptyObject;
        try
        {
            var result = await tool.RunAsync(context, args, ct).ConfigureAwait(false);
            return (new ConversationToolResult(call.Id, result.Data, false), result.NodeIds, result.Action);
        }
        catch (AccessDeniedException)
        {
            return Failed(call, "not allowed");
        }
        catch (NotFoundException e)
        {
            return Failed(call, $"not found: {e.What}");
        }
        catch (DomainException e)
        {
            return Failed(call, $"{e.Code}: {e.Message}");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A bug in a tool must not end the chat or show the model a stack trace.
            return Failed(call, "agent.tool_failed: The tool failed.");
        }
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static (ConversationToolResult, IReadOnlyList<string>, ScreenAction?) Failed(ConversationToolCall call, string message) =>
        (new ConversationToolResult(call.Id, JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["error"] = message }), true), [], null);

    /// <summary>Moves a finished (or cut off) turn into the conversation, answering any call left without a result.</summary>
    private void CloseTurn(Conversation conversation, List<ConversationMessage> turn)
    {
        if (turn.Count == 0)
        {
            return;
        }

        var answered = turn.Where(m => m.ToolResult is not null).Select(m => m.ToolResult!.CallId).ToHashSet(StringComparer.Ordinal);
        foreach (var call in turn[0].ToolCalls.Where(c => !answered.Contains(c.Id)))
        {
            turn.Add(ConversationMessage.Tool(Failed(call, "cancelled: The person stopped the answer.").Item1, clock.Now));
        }

        foreach (var message in turn)
        {
            conversation.Append(message);
        }

        turn.Clear();
    }

    private static ChatMessage ToChat(ConversationMessage m) => m.Role switch
    {
        ConversationRole.User => new ChatMessage(ChatRole.User, m.Text),
        ConversationRole.Assistant => new ChatMessage(ChatRole.Assistant, m.Text,
            m.ToolCalls.Count == 0 ? null : [.. m.ToolCalls.Select(c => new ToolCallPart(c.Id, c.Name, c.Args))]),
        _ => new ChatMessage(ChatRole.Tool, null, null,
            m.ToolResult is { } r ? new ToolResultPart(r.CallId, r.Content, r.IsError) : null),
    };
}
