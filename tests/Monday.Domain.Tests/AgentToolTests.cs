using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monday.Application.Abstractions;
using Monday.Application.Activity;
using Monday.Application.Agent;
using Monday.Application.Agent.Tools;
using Monday.Application.CodeGraph;
using Monday.Application.Decisions;
using Monday.Application.Projects;
using Monday.Application.Traffic;
using Monday.Domain.Access;
using Monday.Domain.Activity;
using Monday.Domain.CodeGraph;
using Monday.Domain.Common;
using Monday.Domain.Decisions;
using Monday.Domain.Projects;
using Monday.Domain.Traffic;
using Monday.Infrastructure;
using Monday.Infrastructure.InMemory;

namespace Monday.Domain.Tests;

/// <summary>The v1 tools are thin: they return what the services allow the asker to see, capped, with node ids.</summary>
internal static class AgentToolTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private const string Endpoint = "endpoint:GET /items";
    private const string Method = "Shop.Items.List";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => AgentToolTests.Now;
    }

    private sealed class World
    {
        public UserId Owner { get; } = new("github:rendee");
        public UserId Ana { get; } = new("github:ana");
        public UserId Mia { get; } = new("github:mia");
        public ProjectId Project { get; private set; }
        public InMemoryGraphStore Graphs { get; } = new();
        public InMemoryTrafficStore Traffic { get; } = new();
        public InMemoryProjectRepository Projects { get; } = new();
        public GraphService Graph { get; private set; } = null!;
        public TrafficService TrafficService { get; private set; } = null!;
        public ActivityService Activity { get; private set; } = null!;
        public DecisionService Decisions { get; private set; } = null!;
        public ProjectService ProjectService { get; private set; } = null!;
        public InMemoryAuditLog Audit { get; } = new();
        public InMemoryActivityStore ActivityStore { get; } = new();

        public ToolContext As(UserId user, Role role) => new(Project, user, role, ToolChannel.Chat);

        public static World Build(int extraNodes = 0, int requests = 3)
        {
            var w = new World();
            var clock = new FixedClock();
            w.ProjectService = new ProjectService(w.Projects, w.Audit, clock);
            w.Graph = new GraphService(w.Graphs, w.ProjectService, clock);
            w.TrafficService = new TrafficService(w.Traffic, new TrafficFeed(), w.ProjectService, w.Audit, clock);
            w.Activity = new ActivityService(w.ActivityStore, w.ProjectService, clock);
            w.Decisions = new DecisionService(new InMemoryDecisionRepository(), w.ProjectService, w.Audit, clock);

            var summary = w.ProjectService.CreateAsync("Shop", w.Owner, default).GetAwaiter().GetResult();
            w.Project = new ProjectId(summary.Id);
            w.ProjectService.SetMemberAsync(w.Project, w.Ana, Role.Debugger, w.Owner, default).GetAwaiter().GetResult();
            w.ProjectService.SetMemberAsync(w.Project, w.Mia, Role.Monitor, w.Owner, default).GetAwaiter().GetResult();

            var code = "return items;";
            List<CodeNode> nodes =
            [
                new() { Id = "app", Kind = NodeKind.App, Name = "Shop" },
                new() { Id = Method, Kind = NodeKind.Method, Name = "List", ParentId = "app", Code = code, CodeHash = CodeNode.HashCode(code) },
                new() { Id = Endpoint, Kind = NodeKind.Endpoint, Name = "GET /items", ParentId = "app", Route = "GET /items" },
                .. Enumerable.Range(0, extraNodes).Select(i => new CodeNode { Id = $"Shop.Extra{i}", Kind = NodeKind.Method, Name = $"Extra{i}", ParentId = "app" }),
            ];
            var finding = new SecurityFinding { Rule = "write-without-auth", Severity = Severity.High, NodeId = Endpoint, Title = "Write without auth", Why = "Anyone can call it.", Fix = "Require authorization." };
            w.Graphs.SaveAsync(CodeGraphSnapshot.Create(w.Project, "abc123", Now, nodes, [new CodeEdge(Endpoint, Method, EdgeKind.HandledBy)], [finding]), default).GetAwaiter().GetResult();

            var project = w.Projects.GetAsync(w.Project, default).GetAwaiter().GetResult()!;
            var hash = project.HashUser("customer-1");
            var env = EnvironmentId.New();
            var traces = Enumerable.Range(0, requests)
                .Select(i => RequestTrace.Record(w.Project, env, $"{i:x32}", Now.AddMinutes(-1).AddSeconds(i), "GET", "/items", 200, 12, ["RequireAuthorization"], hash, null))
                .ToList();
            w.Traffic.AppendAsync(traces, default).GetAwaiter().GetResult();
            return w;
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolResult Run(IAgentTool tool, ToolContext context, string input = "{}") =>
        tool.RunAsync(context, Json(input), default).GetAwaiter().GetResult();

    public static void MonitorNodeDetailHasNoCode()
    {
        var w = World.Build();
        var result = Run(new NodeDetailTool(w.Graph), w.As(w.Mia, Role.Monitor), $$"""{"nodeId":"{{Method}}"}""");

        var node = result.Data.GetProperty("node");
        Assert.Equal("List", node.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("code").ValueKind);
    }

    public static void DebuggerNodeDetailHasCode()
    {
        var w = World.Build();
        var result = Run(new NodeDetailTool(w.Graph), w.As(w.Ana, Role.Debugger), $$"""{"nodeId":"{{Method}}"}""");

        Assert.Equal("return items;", result.Data.GetProperty("node").GetProperty("code").GetString());
        Assert.True(result.NodeIds.Contains(Method), "The node is lit up.");
    }

    public static void RecentRequestsNeverCarryUserHashWithoutReplayRights()
    {
        var w = World.Build();
        var tool = new RecentRequestsTool(w.TrafficService, new FixedClock());

        // Ana may follow users, so the hash is there.
        var withRights = Run(tool, w.As(w.Ana, Role.Debugger));
        Assert.True(withRights.Data.GetProperty("requests")[0].GetProperty("userHash").ValueKind == JsonValueKind.String, "A debugger sees the hash.");

        // A context whose role cannot replay users never gets it, whatever the service returned.
        var without = Run(tool, w.As(w.Ana, Role.Monitor));
        Assert.Equal(JsonValueKind.Null, without.Data.GetProperty("requests")[0].GetProperty("userHash").ValueKind);

        // And a real monitor is refused outright by the service.
        Assert.Throws<AccessDeniedException>(() => Run(tool, w.As(w.Mia, Role.Monitor)), "access.denied");
    }

    public static void StartReplayForUserNeedsReplayUserTraffic()
    {
        var w = World.Build();
        var tool = new StartReplayTool(w.TrafficService, w.Graph);
        var input = $$"""{"since":"{{Now.AddHours(-1):O}}","until":"{{Now:O}}","userId":"customer-1"}""";

        var ok = Run(tool, w.As(w.Ana, Role.Debugger), input);
        Assert.Equal("start_replay", ok.Action!.Type);
        Assert.True(ok.NodeIds.Contains(Endpoint), "Replay lights up the endpoints it touched.");
        Assert.True(w.Audit.ListAsync(w.Project, default).GetAwaiter().GetResult().Count(e => e.Action == "traffic.user_replay") == 1, "A user replay is audited.");

        Assert.Throws<AccessDeniedException>(() => Run(tool, w.As(w.Mia, Role.Monitor), input), "access.denied");
    }

    public static void DraftDecisionLandsAsProposed()
    {
        var w = World.Build();
        const string input = """
            {"area":"Stack","title":"Live store","context":"Where traffic lives.",
             "options":[{"key":"neon","title":"Neon","tradeOffs":"Free"},{"key":"sqlite","title":"SQLite","tradeOffs":"Lost on redeploy"}],
             "recommendedKey":"neon"}
            """;

        var result = Run(new DraftDecisionTool(w.Decisions), w.As(w.Ana, Role.Debugger), input);

        Assert.Equal("Proposed", result.Data.GetProperty("status").GetString());
        var stored = w.Decisions.ListAsync(w.Project, w.Owner, default).GetAwaiter().GetResult();
        Assert.Equal(1, stored.Count);
        Assert.Equal(w.Ana, stored[0].DraftedBy!.Value);

        var drafted = w.Audit.ListAsync(w.Project, default).GetAwaiter().GetResult().Single(e => e.Action == "decision.drafted");
        Assert.Equal(w.Ana, drafted.Actor);
        Assert.Equal("Live store", drafted.Detail);
    }

    public static void MonitorCannotDraftDecision()
    {
        var w = World.Build();
        var proposal = new DecisionProposal(DecisionArea.Stack, "Live store", "Where traffic lives.",
            [new("neon", "Neon", "Free"), new("sqlite", "SQLite", "Lost")], "neon", null);

        Assert.Throws<AccessDeniedException>(
            () => w.Decisions.DraftAsync(w.Project, proposal, w.Mia, default).GetAwaiter().GetResult(), "access.denied");
    }

    public static void ToolResultsCarryNodeIds()
    {
        var w = World.Build();
        var result = Run(new FindNodesTool(w.Graph), w.As(w.Mia, Role.Monitor), """{"query":"items"}""");

        Assert.True(result.NodeIds.Contains(Endpoint), "find_nodes returns the ids of its matches.");
        Assert.Equal(result.NodeIds.Count, result.Data.GetProperty("nodes").GetArrayLength());
    }

    public static void ListsCappedAtFifty()
    {
        var w = World.Build(extraNodes: 80, requests: 80);

        var nodes = Run(new FindNodesTool(w.Graph), w.As(w.Mia, Role.Monitor), """{"query":"Extra"}""");
        Assert.Equal(50, nodes.Data.GetProperty("nodes").GetArrayLength());
        Assert.Equal(50, nodes.NodeIds.Count);
        Assert.True(nodes.Data.GetProperty("truncated").GetBoolean(), "The result says it was cut.");

        var requests = Run(new RecentRequestsTool(w.TrafficService, new FixedClock()), w.As(w.Ana, Role.Debugger));
        Assert.Equal(50, requests.Data.GetProperty("requests").GetArrayLength());
    }

    public static void NodeCodeIsCappedAt4000Characters()
    {
        var w = World.Build();
        var big = new string('x', 9000);
        var node = new CodeNode { Id = "Shop.Big", Kind = NodeKind.Method, Name = "Big", ParentId = "app", Code = big, CodeHash = CodeNode.HashCode(big) };
        var snapshot = w.Graphs.LatestAsync(w.Project, default).GetAwaiter().GetResult()!;
        w.Graphs.SaveAsync(CodeGraphSnapshot.Create(w.Project, "def456", Now, [.. snapshot.Nodes, node], snapshot.Edges, snapshot.Findings), default).GetAwaiter().GetResult();

        var result = Run(new NodeDetailTool(w.Graph), w.As(w.Ana, Role.Debugger), """{"nodeId":"Shop.Big"}""");

        Assert.Equal(4000, result.Data.GetProperty("node").GetProperty("code").GetString()!.Length);
        Assert.True(result.Data.GetProperty("codeTruncated").GetBoolean(), "The result says the code was cut.");
    }

    public static void SecurityFindingsHiddenFromMonitorServiceView()
    {
        var w = World.Build();
        var tool = new SecurityFindingsTool(w.Graph);

        var debugger = Run(tool, w.As(w.Ana, Role.Debugger));
        Assert.Equal(1, debugger.Data.GetProperty("findings").GetArrayLength());
        Assert.True(debugger.NodeIds.Contains(Endpoint), "A finding lights up its node.");

        var monitor = Run(tool, w.As(w.Mia, Role.Monitor));
        Assert.Equal(0, monitor.Data.GetProperty("findings").GetArrayLength());
    }

    public static void ScreenToolsRejectUnknownNodes()
    {
        var w = World.Build();
        var ctx = w.As(w.Ana, Role.Debugger);

        var focus = Run(new FocusNodeTool(w.Graph), ctx, $$"""{"nodeId":"{{Endpoint}}"}""");
        Assert.Equal("focus_node", focus.Action!.Type);
        Assert.True(focus.NodeIds.SequenceEqual([Endpoint]), "Focus names its node.");

        Assert.Throws<NotFoundException>(() => Run(new FocusNodeTool(w.Graph), ctx, """{"nodeId":"Nope"}"""));
        Assert.Throws<NotFoundException>(() => Run(new HighlightPathTool(w.Graph), ctx, $$"""{"nodeIds":["{{Endpoint}}","Nope"]}"""));
        Assert.Throws<DomainException>(() => Run(new SetLensTool(), ctx, """{"lens":"sparkles"}"""), "agent.input");
        Assert.Equal("set_lens", Run(new SetLensTool(), ctx, """{"lens":"security"}""").Action!.Type);
    }

    public static void ListProjectsIsMcpOnlyAndListsMine()
    {
        var w = World.Build();
        var tool = new ListProjectsTool(w.ProjectService);
        Assert.Equal(ToolChannel.Mcp, tool.OnlyOn!.Value);
        Assert.Equal(0, tool.Requires.Count);

        var result = Run(tool, new ToolContext(default, w.Mia, Role.Monitor, ToolChannel.Mcp));
        Assert.Equal(1, result.Data.GetProperty("projects").GetArrayLength());
    }

    public static void RecentActivityListsNodesTouched()
    {
        var w = World.Build();
        w.ActivityStore.AppendAsync([ActivityEvent.Record(w.Project, "s1", Now.AddMinutes(-5), ActivityKind.Edit, "Edited List", [Method])], default).GetAwaiter().GetResult();

        var result = Run(new RecentActivityTool(w.Activity, new FixedClock()), w.As(w.Mia, Role.Monitor));

        Assert.Equal(1, result.Data.GetProperty("events").GetArrayLength());
        Assert.True(result.NodeIds.SequenceEqual([Method]), "The event's nodes light up.");
    }

    public static void RecentRequestsReturnsTheNewestFiftyOfABusyWindow()
    {
        var w = World.Build(requests: 80);

        var result = Run(new RecentRequestsTool(w.TrafficService, new FixedClock()), w.As(w.Ana, Role.Debugger));

        var requests = result.Data.GetProperty("requests");
        Assert.Equal(50, requests.GetArrayLength());
        Assert.True(result.Data.GetProperty("truncated").GetBoolean(), "The window held more than 50.");

        // Fixture traces run one second apart from -1 minute, and the window ends at Now, so offsets 0..60 are in it;
        // the 11 oldest (0..10) must be the ones dropped.
        var first = requests[0].GetProperty("at").GetDateTimeOffset();
        var last = requests[49].GetProperty("at").GetDateTimeOffset();
        Assert.Equal(Now.AddMinutes(-1).AddSeconds(11), first);
        Assert.Equal(Now.AddMinutes(-1).AddSeconds(60), last);
    }

    public static void NodeDetailShapeOnlyDropsCodeEvenForARealDebugger()
    {
        var w = World.Build();

        // Ana really is a Debugger, so the service would hand over source; a Monitor context must not.
        var result = Run(new NodeDetailTool(w.Graph), w.As(w.Ana, Role.Monitor), $$"""{"nodeId":"{{Method}}"}""");

        Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("node").GetProperty("code").ValueKind);
    }

    public static void DraftWithSupersedesStoresIt()
    {
        var w = World.Build();
        var earlier = w.Decisions.DraftAsync(w.Project, new DecisionProposal(DecisionArea.Stack, "Old", "Old call.", [new("a", "A", "x"), new("b", "B", "y")], "a", null), w.Ana, default).GetAwaiter().GetResult();
        var input = $$"""
            {"area":"Stack","title":"New","context":"Changed our mind.","supersedes":"{{earlier.Id.Value}}",
             "options":[{"key":"a","title":"A","tradeOffs":"x"},{"key":"b","title":"B","tradeOffs":"y"}],"recommendedKey":"b"}
            """;

        Run(new DraftDecisionTool(w.Decisions), w.As(w.Ana, Role.Debugger), input);

        var stored = w.Decisions.ListAsync(w.Project, w.Owner, default).GetAwaiter().GetResult().Single(d => d.Title == "New");
        Assert.Equal(earlier.Id, stored.Supersedes!.Value);
    }

    public static void ListDecisionsReturnsTheApiFields()
    {
        var w = World.Build();
        w.Decisions.DraftAsync(w.Project, new DecisionProposal(DecisionArea.Stack, "Old", "Old call.", [new("a", "A", "x"), new("b", "B", "y")], "a", null), w.Ana, default).GetAwaiter().GetResult();

        var item = Run(new ListDecisionsTool(w.Decisions), w.As(w.Mia, Role.Monitor)).Data.GetProperty("decisions")[0];

        foreach (var field in new[] { "supersedes", "resolvedBy", "resolvedAt" })
        {
            Assert.True(item.TryGetProperty(field, out _), $"{field} is returned.");
        }

        Assert.Equal("github:ana", item.GetProperty("draftedBy").GetString());
    }

    public static void RecentRequestsServiceNeedsInspectRequests()
    {
        var w = World.Build();

        Assert.Throws<AccessDeniedException>(
            () => w.TrafficService.RecentAsync(w.Project, Now.AddHours(-1), Now, 10, w.Mia, default).GetAwaiter().GetResult(), "access.denied");
        Assert.Equal(3, w.TrafficService.RecentAsync(w.Project, Now.AddHours(-1), Now, 10, w.Ana, default).GetAwaiter().GetResult().Count);
    }

    public static void UnknownFilterValuesAreToolErrors()
    {
        var w = World.Build();
        var ctx = w.As(w.Ana, Role.Debugger);

        Assert.Throws<DomainException>(() => Run(new ListDecisionsTool(w.Decisions), ctx, """{"status":"Maybe"}"""), "agent.input");
        Assert.Throws<DomainException>(() => Run(new FindNodesTool(w.Graph), ctx, """{"query":"x","kind":"Widget"}"""), "agent.input");
        Assert.Throws<DomainException>(() => Run(new SecurityFindingsTool(w.Graph), ctx, """{"severity":"Dire"}"""), "agent.input");
    }

    public static void RegistrationOffersTheSpecTable()
    {
        var services = new ServiceCollection();
        services.AddMonday(new ConfigurationBuilder().Build());
        services.AddAgentTools();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ToolRegistry>();

        Assert.Equal(6, registry.For(Role.Monitor, ToolChannel.Chat).Count);
        Assert.Equal(13, registry.For(Role.Debugger, ToolChannel.Chat).Count);
        Assert.Equal(10, registry.For(Role.Debugger, ToolChannel.Mcp).Count);
        Assert.True(registry.Find("list_projects", Role.Monitor, ToolChannel.Chat) is null, "list_projects is not offered in chat.");
        Assert.True(registry.For(Role.Owner, ToolChannel.Chat).All(t => t.Requires.Contains(Permission.ChatAsk) || t.Requires.Contains(Permission.ChatAct)), "Every chat tool needs chat rights.");
    }
}
