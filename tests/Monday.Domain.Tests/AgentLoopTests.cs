using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monday.Application.Abstractions;
using Monday.Application.Agent;
using Monday.Application.Agent.Tools;
using Monday.Application.CodeGraph;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Agent;
using Monday.Domain.CodeGraph;
using Monday.Domain.Common;
using Monday.Domain.Projects;
using Monday.Domain.Tests.Fakes;
using Monday.Infrastructure.InMemory;
using Monday.Infrastructure.Secrets;

namespace Monday.Domain.Tests;

/// <summary>The agent loop: streams text, runs tools as the asker, stops at the step cap, and records usage.</summary>
internal static class AgentLoopTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private const string Endpoint = "endpoint:GET /items";
    private const string PlainKey = "sk-test-PLAINSECRET-1234";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => AgentLoopTests.Now;
    }

    private sealed class FakeFactory(IChatModel model) : IChatModelFactory
    {
        public string? Key { get; private set; }

        public IChatModel Create(ModelSettings settings, string apiKey)
        {
            Key = apiKey;
            return model;
        }
    }

    private sealed class World
    {
        public UserId Owner { get; } = new("github:rendee");
        public UserId Ana { get; } = new("github:ana");
        public UserId Mia { get; } = new("github:mia");
        public UserId Stranger { get; } = new("github:stranger");
        public ProjectId Project { get; private set; }
        public InMemoryProjectRepository Projects { get; } = new();
        public InMemoryModelUsageStore Usage { get; } = new();
        public InMemoryConversationStore Conversations { get; } = new();
        public ScriptedChatModel Model { get; private init; } = null!;
        public FakeFactory Factory { get; private init; } = null!;
        public AgentService Agent { get; private set; } = null!;
        public AesGcmSecretBox Box { get; private set; } = null!;

        public static World Build(ScriptedChatModel model, bool configured = true, bool secrets = true, long? cap = null)
        {
            var w = new World { Model = model, Factory = new FakeFactory(model) };
            var clock = new FixedClock();
            var audit = new InMemoryAuditLog();
            var key = secrets ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
            var box = new AesGcmSecretBox(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MONDAY_SECRETS_KEY"] = key }).Build());
            w.Box = box;

            var projects = new ProjectService(w.Projects, audit, clock);
            w.Project = new ProjectId(projects.CreateAsync("Shop", w.Owner, default).GetAwaiter().GetResult().Id);
            projects.SetMemberAsync(w.Project, w.Ana, Role.Debugger, w.Owner, default).GetAwaiter().GetResult();
            projects.SetMemberAsync(w.Project, w.Mia, Role.Monitor, w.Owner, default).GetAwaiter().GetResult();

            if (configured)
            {
                var project = w.Projects.GetAsync(w.Project, default).GetAwaiter().GetResult()!;
                var cipher = secrets ? box.Seal(PlainKey) : "v1:a:b:c";
                project.SetModel(ModelSettings.Create(ModelProvider.Anthropic, "claude-sonnet-4-5", null, cipher, "1234", cap), w.Owner, Now);
                w.Projects.SaveAsync(project, default).GetAwaiter().GetResult();
            }

            var graphs = new InMemoryGraphStore();
            List<CodeNode> nodes =
            [
                new() { Id = "app", Kind = NodeKind.App, Name = "Shop" },
                new() { Id = Endpoint, Kind = NodeKind.Endpoint, Name = "GET /items", ParentId = "app", Route = "GET /items" },
            ];
            graphs.SaveAsync(CodeGraphSnapshot.Create(w.Project, "abc123", Now, nodes, [], []), default).GetAwaiter().GetResult();
            var graph = new GraphService(graphs, projects, clock);

            var registry = new ToolRegistry([new FindNodesTool(graph), new FocusNodeTool(graph), new SetLensTool()]);
            w.Agent = new AgentService(w.Projects, w.Conversations, w.Usage, box, w.Factory, registry, clock);
            return w;
        }

        public List<AgentEvent> Chat(UserId user, string message, Guid? conversation = null, string? selected = null) =>
            Collect(Agent.ChatAsync(Project, user, new ChatInput(conversation, message, selected), default));

        public Usage MonthUsage() => Usage.MonthAsync(Project, "2026-10", default).GetAwaiter().GetResult();
    }

    private sealed class FailingModel : IChatModel
    {
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextDelta("Partial");
            await Task.Yield();
            throw new ModelException("model.rate_limited", "The provider is rate limiting this key.");
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<AgentEvent> Collect(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        var e = events.GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                list.Add(e.Current);
            }
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return list;
    }

    private static IReadOnlyList<ModelEvent> Answer(string text, int input = 10, int output = 5) =>
        [new TextDelta(text), new Finished(StopReason.EndTurn, new Usage(input, output))];

    private static IReadOnlyList<ModelEvent> Call(string id, string name, string args, int input = 10, int output = 5) =>
        [new ToolCall(id, name, Json(args)), new Finished(StopReason.ToolUse, new Usage(input, output))];

    public static void AnswersWithoutTools()
    {
        var w = World.Build(new ScriptedChatModel(Answer("Hello there.", 100, 20)));

        var events = w.Chat(w.Ana, "Hi");

        Assert.Equal("Hello there.", ((AgentText)events[0]).Text);
        var done = (AgentDone)events[^1];
        Assert.Equal(new Usage(100, 20), done.Usage);
        Assert.Equal(new Usage(100, 20), w.MonthUsage());
        Assert.Equal(PlainKey, w.Factory.Key);

        var request = w.Model.Requests.Single();
        Assert.True(request.Instructions.Contains("Shop", StringComparison.Ordinal), "Instructions name the project.");
        Assert.True(request.Instructions.Contains("Debugger", StringComparison.Ordinal), "Instructions name the asker's role.");
        Assert.True(request.Instructions.Contains("read-only", StringComparison.Ordinal), "Instructions say Monday is read-only.");
        Assert.True(!request.Instructions.Contains(PlainKey, StringComparison.Ordinal), "The key never reaches the instructions.");
        Assert.Equal("Hi", request.Messages.Single().Text);

        var saved = w.Conversations.GetAsync(new ConversationId(done.ConversationId), default).GetAwaiter().GetResult()!;
        Assert.Equal(2, saved.Messages.Count);
        Assert.Equal("Hello there.", saved.Messages[1].Text);
    }

    public static void RunsToolThenAnswers()
    {
        var w = World.Build(new ScriptedChatModel(
            Call("c1", "find_nodes", """{"query":"items"}"""),
            Answer($"It is `{Endpoint}`.")));

        var events = w.Chat(w.Mia, "Where is items?");

        var tool = events.OfType<AgentTool>().Single();
        Assert.Equal("find_nodes", tool.Name);
        Assert.True(!tool.IsError, "The tool ran.");
        Assert.True(tool.NodeIds.Contains(Endpoint), "The tool event carries its node ids.");

        Assert.Equal(2, w.Model.Requests.Count);
        var second = w.Model.Requests[1].Messages;
        Assert.Equal(3, second.Count);
        Assert.Equal("c1", second[1].ToolCalls!.Single().Id);
        var result = second[2].ToolResult!;
        Assert.Equal("c1", result.CallId);
        Assert.True(!result.IsError, "The result is not an error.");
        Assert.True(result.Content.GetRawText().Contains(Endpoint, StringComparison.Ordinal), "The second request contains the tool result.");
        Assert.Equal(new Usage(20, 10), ((AgentDone)events[^1]).Usage);
    }

    public static void StopsAfterEightToolSteps()
    {
        var turns = Enumerable.Range(0, 8).Select(i => Call($"c{i}", "set_lens", """{"lens":"security"}""")).ToArray();
        var w = World.Build(new ScriptedChatModel(turns));

        var events = w.Chat(w.Ana, "Loop forever");

        Assert.Equal(AgentService.MaxToolSteps, w.Model.Requests.Count);
        Assert.Equal(8, events.OfType<AgentTool>().Count());
        var error = (AgentError)events[^1];
        Assert.Equal("agent.step_cap", error.Code);
        Assert.Equal(new Usage(80, 40), w.MonthUsage());
    }

    public static void HiddenToolBecomesToolError()
    {
        var w = World.Build(new ScriptedChatModel(
            Call("c1", "focus_node", $$"""{"nodeId":"{{Endpoint}}"}"""),
            Answer("I can't move the view.")));

        var events = w.Chat(w.Mia, "Focus items");

        var tool = events.OfType<AgentTool>().Single();
        Assert.True(tool.IsError, "A Monitor cannot run a screen action.");
        Assert.Equal(0, events.OfType<AgentAction>().Count());
        Assert.True(w.Model.Requests[0].Tools.All(t => t.Name != "focus_node"), "A hidden tool is never offered.");
        var result = w.Model.Requests[1].Messages[2].ToolResult!;
        Assert.True(result.IsError, "The model is told the call failed.");
        Assert.True(events[^1] is AgentDone, "The turn still finishes.");
    }

    public static void InvalidArgumentsAndToolFailuresBecomeToolErrors()
    {
        var w = World.Build(new ScriptedChatModel(
            Call("c1", "find_nodes", """{"_invalid":"{\"query\": \"it"}"""),
            Call("c2", "focus_node", """{"nodeId":"Nope"}"""),
            Call("c3", "set_lens", """{"lens":"sparkles"}"""),
            Answer("Sorry.")));

        var events = w.Chat(w.Ana, "Try things");

        Assert.True(events.OfType<AgentTool>().All(t => t.IsError), "Every failed call is a tool error.");
        var results = w.Model.Requests[3].Messages.Where(m => m.ToolResult is not null).Select(m => m.ToolResult!).ToList();
        Assert.Equal(3, results.Count);
        Assert.True(results.All(r => r.IsError), "Each result is marked as an error.");
        Assert.True(results[1].Content.GetRawText().Contains("not found", StringComparison.Ordinal), "Not found is short.");
        Assert.True(results[2].Content.GetRawText().Contains("agent.input", StringComparison.Ordinal), "Input errors carry their code.");
        Assert.True(results.All(r => !r.Content.GetRawText().Contains(" at Monday.", StringComparison.Ordinal)), "No stack trace reaches the model.");
    }

    public static void CancellationStopsLoopAndRecordsUsage()
    {
        var w = World.Build(new ScriptedChatModel(
            Call("c1", "set_lens", """{"lens":"security"}""", 30, 7),
            Answer("Never reached.")));
        using var cts = new CancellationTokenSource();
        var seen = new List<AgentEvent>();

        Assert.Throws<OperationCanceledException>(() =>
        {
            var e = w.Agent.ChatAsync(w.Project, w.Ana, new ChatInput(null, "Show security", null), cts.Token).GetAsyncEnumerator();
            try
            {
                while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    seen.Add(e.Current);
                    if (e.Current is AgentTool)
                    {
                        cts.Cancel();
                    }
                }
            }
            finally
            {
                e.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

        Assert.Equal(1, w.Model.Requests.Count);
        Assert.Equal(new Usage(30, 7), w.MonthUsage());
        Assert.True(seen.All(e => e is not AgentDone), "A cancelled turn is not done.");

        var saved = w.Conversations.ListAsync(w.Project, w.Ana, default).GetAwaiter().GetResult().Single();
        var conversation = w.Conversations.GetAsync(new ConversationId(saved.Id), default).GetAwaiter().GetResult()!;
        Assert.Equal(3, conversation.Messages.Count);
        Assert.Equal(ConversationRole.Tool, conversation.Messages[2].Role);
    }

    public static void CapReachedRefusesBeforeModelCall()
    {
        var w = World.Build(new ScriptedChatModel(Answer("Never.")), cap: 100);
        w.Usage.AddAsync(w.Project, "2026-10", new Usage(60, 40), default).GetAwaiter().GetResult();

        var events = w.Chat(w.Ana, "Hi");

        Assert.Equal("model.cap_reached", ((AgentError)events.Single()).Code);
        Assert.Equal(0, w.Model.Requests.Count);
    }

    public static void MissingSettingsOrKeysRefuseBeforeModelCall()
    {
        var none = World.Build(new ScriptedChatModel(Answer("Never.")), configured: false);
        Assert.Equal("model.not_configured", ((AgentError)none.Chat(none.Ana, "Hi").Single()).Code);
        Assert.Equal(0, none.Model.Requests.Count);

        var disabled = World.Build(new ScriptedChatModel(Answer("Never.")), secrets: false);
        Assert.Equal("model.keys_disabled", ((AgentError)disabled.Chat(disabled.Ana, "Hi").Single()).Code);
        Assert.Equal(0, disabled.Model.Requests.Count);
    }

    public static void NonMemberIsRefusedBeforeModelCall()
    {
        var w = World.Build(new ScriptedChatModel(Answer("Never.")));

        Assert.Throws<NotFoundException>(() => w.Chat(w.Stranger, "Hi"));
        Assert.Throws<DomainException>(() => w.Chat(w.Ana, new string('x', 4001)), "conversation.message_too_long");
        Assert.Equal(0, w.Model.Requests.Count);
    }

    public static void ModelFailureEndsTheTurnWithAnError()
    {
        var w = World.Build(new ScriptedChatModel(Answer("Never.")));
        var failing = new FailingModel();
        var agent = new AgentService(w.Projects, w.Conversations, w.Usage, w.Box, new FakeFactory(failing), new ToolRegistry([]), new FixedClock());

        var events = Collect(agent.ChatAsync(w.Project, w.Ana, new ChatInput(null, "Hi", null), default));

        Assert.Equal("Partial", ((AgentText)events[0]).Text);
        var error = (AgentError)events[^1];
        Assert.Equal("model.rate_limited", error.Code);
        Assert.Equal(2, events.Count);
    }

    public static void ConversationVisibleOnlyToOwner()
    {
        var w = World.Build(new ScriptedChatModel(Answer("First."), Answer("Second.")));
        var done = (AgentDone)w.Chat(w.Ana, "Remember this")[^1];

        Assert.Equal(1, w.Agent.ListConversationsAsync(w.Project, w.Ana, default).GetAwaiter().GetResult().Count);
        Assert.Equal(0, w.Agent.ListConversationsAsync(w.Project, w.Owner, default).GetAwaiter().GetResult().Count);
        Assert.Throws<NotFoundException>(() => w.Agent.GetConversationAsync(w.Project, done.ConversationId, w.Owner, default).GetAwaiter().GetResult());
        Assert.Throws<NotFoundException>(() => w.Chat(w.Owner, "Let me in", done.ConversationId));

        // The owner continues it, and the model sees the earlier turn.
        w.Chat(w.Ana, "And now?", done.ConversationId);
        Assert.Equal(3, w.Model.Requests[1].Messages.Count);
        var view = w.Agent.GetConversationAsync(w.Project, done.ConversationId, w.Ana, default).GetAwaiter().GetResult();
        Assert.Equal("Remember this", view.Title);
        Assert.Equal(4, view.Messages.Count);
    }

    public static void ScreenActionEmittedAsAction()
    {
        var w = World.Build(new ScriptedChatModel(
            Call("c1", "set_lens", """{"lens":"security"}"""),
            Answer("Switched.")));

        var events = w.Chat(w.Ana, "Show security", selected: Endpoint);

        var action = events.OfType<AgentAction>().Single();
        Assert.Equal("set_lens", action.Action.Type);
        Assert.Equal("security", action.Action.Args.GetProperty("lens").GetString());
        Assert.True(events.IndexOf(action) > events.FindIndex(e => e is AgentTool), "The action follows its tool event.");
        Assert.True(w.Model.Requests[0].Instructions.Contains(Endpoint, StringComparison.Ordinal), "The selected node is in the instructions.");
    }
}
