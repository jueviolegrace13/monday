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

/// <summary>The turn has begun in this conversation; the first event of every turn.</summary>
public sealed record AgentStarted(Guid ConversationId) : AgentEvent;

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

    /// <summary>Most stored messages (user and assistant text) sent back to the model when a conversation continues.</summary>
    public const int MaxHistoryMessages = 20;

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

        // The question is stored first: the id the viewer is given is real, and an append from another tab shows now.
        var run = new Run(conversation);
        if (!await TrySaveAsync(run).ConfigureAwait(false))
        {
            yield return Busy;
            yield break;
        }

        yield return new AgentStarted(conversation.Id.Value);

        // The turns run in an inner iterator, so a failure can be caught here (no yield may sit inside a catch'd try):
        // what finished is saved, quietly, and the original exception is the one that surfaces.
        var turns = TurnsAsync(run, project, role, selected, model, ct).GetAsyncEnumerator(ct);
        var faulted = false;
        try
        {
            while (true)
            {
                AgentEvent next;
                try
                {
                    if (!await turns.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    next = turns.Current;
                }
                catch (Exception)
                {
                    faulted = true;
                    await SaveQuietlyAsync(run).ConfigureAwait(false);
                    throw;
                }

                yield return next;
            }
        }
        finally
        {
            await turns.DisposeAsync().ConfigureAwait(false);

            // The viewer stopped reading (Stop, closed tab) between events: keep what finished.
            if (!faulted)
            {
                await SaveQuietlyAsync(run).ConfigureAwait(false);
            }
        }
    }

    private async IAsyncEnumerable<AgentEvent> TurnsAsync(Run run, Project project, Role role, string? selected, IChatModel model, [EnumeratorCancellation] CancellationToken ct)
    {
        var conversation = run.Conversation;
        var history = Replay(conversation);
        var instructions = AgentInstructions.Build(project, role, selected);
        var offered = tools.For(role, ToolChannel.Chat).Select(t => t.ToSpec()).ToList();
        var context = new ToolContext(project.Id, conversation.Owner, role, ToolChannel.Chat);
        var month = ModelSettingsService.MonthOf(clock.Now);

        for (var steps = 0; ; steps++)
        {
            if (steps == MaxToolSteps)
            {
                yield return new AgentError("agent.step_cap", $"Stopped after {MaxToolSteps} tool steps. Ask again to carry on from here.");
                yield break;
            }

            if (steps > 0 && await CapReachedAsync(project, ct).ConfigureAwait(false) is { } capped)
            {
                yield return capped;
                yield break;
            }

            ct.ThrowIfCancellationRequested();
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
                        failed = error ?? failed;
                        break;
                    }

                    switch (stream.Current)
                    {
                        case TextDelta { Text.Length: > 0 } delta:
                            run.Streaming.Append(delta.Text);
                            yield return new AgentText(delta.Text);
                            break;
                        case ToolCall call:
                            calls.Add(new ConversationToolCall(call.Id, call.Name, call.Args));
                            break;
                        case Finished finished:
                            used = finished.Usage;

                            // For example a malformed tool call or a content filter: no provider text reaches the viewer.
                            if (finished.Reason == StopReason.Error)
                            {
                                failed = Stopped;
                            }

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
                    run.Total = new Usage(run.Total.InputTokens + used.InputTokens, run.Total.OutputTokens + used.OutputTokens);
                    await usage.AddAsync(project.Id, month, used, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (failed is not null)
            {
                // Partial text is kept (CloseTurn), the model's failure is what the viewer sees.
                CloseTurn(run);
                await TrySaveAsync(run).ConfigureAwait(false);
                yield return failed;
                yield break;
            }

            run.Turn.Add(ConversationMessage.Assistant(run.Streaming.ToString(), calls, clock.Now));
            run.Streaming.Clear();
            foreach (var call in calls)
            {
                var (result, nodeIds, action) = await RunToolAsync(call, context, ct).ConfigureAwait(false);
                run.Turn.Add(ConversationMessage.Tool(result, clock.Now));
                yield return new AgentTool(call.Name, nodeIds, result.IsError);
                if (action is not null)
                {
                    yield return new AgentAction(action);
                }
            }

            // This turn's own tool calls and results go to the model; stored ones from earlier messages never do.
            history.AddRange(run.Turn.Select(ToChat));
            CloseTurn(run);
            if (!await TrySaveAsync(run).ConfigureAwait(false))
            {
                yield return Busy;
                yield break;
            }

            if (calls.Count == 0)
            {
                yield return new AgentDone(conversation.Id.Value, run.Total);
                yield break;
            }
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
        if (project.Model is null)
        {
            return new AgentError("model.not_configured", "No model is set up for this project yet. An Owner or Admin can add one in settings.");
        }

        if (!secrets.Enabled)
        {
            return new AgentError("model.keys_disabled", "Chat is off: Monday has no MONDAY_SECRETS_KEY to open the model key.");
        }

        return await CapReachedAsync(project, ct).ConfigureAwait(false);
    }

    /// <summary>Checked before the first model call and again before each tool step.</summary>
    private async Task<AgentError?> CapReachedAsync(Project project, CancellationToken ct)
    {
        if (project.Model?.MonthlyTokenCap is { } cap)
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

    /// <summary>
    /// Moves a finished (or cut off) turn into the conversation: text that was streaming when the turn stopped is kept,
    /// and each call left without a result gets a "cancelled" one, so the stored record stays whole.
    /// </summary>
    private void CloseTurn(Run run)
    {
        if (run.Turn.Count == 0 && run.Streaming.Length > 0)
        {
            run.Turn.Add(ConversationMessage.Assistant(run.Streaming.ToString().TrimEnd(), null, clock.Now));
        }

        run.Streaming.Clear();
        if (run.Turn.Count == 0)
        {
            return;
        }

        var answered = run.Turn.Where(m => m.ToolResult is not null).Select(m => m.ToolResult!.CallId).ToHashSet(StringComparer.Ordinal);
        foreach (var call in run.Turn[0].ToolCalls.Where(c => !answered.Contains(c.Id)))
        {
            run.Turn.Add(ConversationMessage.Tool(Failed(call, "cancelled: The person stopped the answer.").Item1, clock.Now));
        }

        foreach (var message in run.Turn)
        {
            run.Conversation.Append(message);
        }

        run.Turn.Clear();
    }

    /// <summary>Saves what is unsaved. False when another tab appended first: this turn lost, and nothing more is saved.</summary>
    private async Task<bool> TrySaveAsync(Run run)
    {
        if (run.Abandoned || !run.Conversation.HasUnsaved)
        {
            return !run.Abandoned;
        }

        try
        {
            await conversations.SaveAsync(run.Conversation, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (ConversationConflictException)
        {
            run.Abandoned = true;
            return false;
        }
    }

    /// <summary>The last save on the way out. Best effort: it never replaces the exception (or Stop) that ended the turn.</summary>
    private async Task SaveQuietlyAsync(Run run)
    {
        try
        {
            CloseTurn(run);
            await TrySaveAsync(run).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallowed on purpose: the original exception is rethrown by the caller.
        }
    }

    /// <summary>
    /// What a continued conversation sends the model: the last <see cref="MaxHistoryMessages"/> user and assistant texts,
    /// starting with a user message. Stored tool calls and results are never replayed, so a member whose role dropped
    /// cannot get back data their old role saw; the model runs tools again under the current role.
    /// </summary>
    private static List<ChatMessage> Replay(Conversation conversation) =>
        [.. conversation.Messages
            .Where(m => m.Role != ConversationRole.Tool && !string.IsNullOrEmpty(m.Text))
            .Select(m => new ChatMessage(m.Role == ConversationRole.User ? ChatRole.User : ChatRole.Assistant, m.Text))
            .TakeLast(MaxHistoryMessages)
            .SkipWhile(m => m.Role != ChatRole.User)];

    private static AgentError Busy => new("agent.busy", "This conversation changed in another tab. Reload it and ask again.");

    private static AgentError Stopped => new("model.stopped", "The model stopped before it could answer. Ask again, or put the question another way.");

    /// <summary>The state one chat request carries between its turns.</summary>
    private sealed class Run(Conversation conversation)
    {
        public Conversation Conversation { get; } = conversation;

        /// <summary>The current turn: the assistant message and the tool results that answer it.</summary>
        public List<ConversationMessage> Turn { get; } = [];

        /// <summary>Text of the model stream in progress, kept if the turn is cut off.</summary>
        public StringBuilder Streaming { get; } = new();

        public Usage Total { get; set; } = new(0, 0);

        /// <summary>Another tab saved first; this request saves nothing more.</summary>
        public bool Abandoned { get; set; }
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
