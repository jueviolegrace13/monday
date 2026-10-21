using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Monday.Api.Endpoints;
using Monday.Api.Security;
using Monday.Application.Abstractions;
using Monday.Application.Access;
using Monday.Application.Agent;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Projects;
using Monday.Infrastructure;
using Role = Monday.Domain.Access.Role;

namespace Monday.Domain.Tests;

/// <summary>
/// The MCP endpoint over real HTTP: a small Kestrel host on a free loopback port, wired with the same
/// <c>AddMondayMcp</c>/<c>MapMondayMcp</c> as Program, and the SDK's own client.
/// </summary>
internal static class McpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly UserId Ana = new("github:ana");
    private static readonly UserId Owner = new("github:rendee");
    private static readonly string[] ScreenTools = ["focus_node", "highlight_path", "set_lens", "start_replay"];

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset Now { get; set; } = McpTests.Now;
    }

    private sealed class Host : IDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, MovableClock clock, Uri endpoint, ProjectId shop, ProjectId blog)
        {
            _app = app;
            Clock = clock;
            Endpoint = endpoint;
            Shop = shop;
            Blog = blog;
        }

        public MovableClock Clock { get; }

        public Uri Endpoint { get; }

        /// <summary>Ana is a Debugger here.</summary>
        public ProjectId Shop { get; }

        /// <summary>Ana is a Monitor here.</summary>
        public ProjectId Blog { get; }

        public static Host Start()
        {
            var clock = new MovableClock();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.Configuration["ConnectionStrings:Monday"] = string.Empty; // always in memory
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddMonday(builder.Configuration);
            builder.Services.AddAgentTools();
            builder.Services.AddSingleton<IClock>(clock);
            builder.Services.AddMondayUserAuthentication(builder.Environment);
            builder.Services.AddMondayPersonalTokenAuthentication();
            builder.Services.AddMondayMcp();

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapMondayMcp();
            app.StartAsync().GetAwaiter().GetResult();

            using var scope = app.Services.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
            var shop = new ProjectId(projects.CreateAsync("Shop", Owner, default).GetAwaiter().GetResult().Id);
            var blog = new ProjectId(projects.CreateAsync("Blog", Owner, default).GetAwaiter().GetResult().Id);
            projects.SetMemberAsync(shop, Ana, Role.Debugger, Owner, default).GetAwaiter().GetResult();
            projects.SetMemberAsync(blog, Ana, Role.Monitor, Owner, default).GetAwaiter().GetResult();

            return new Host(app, clock, new Uri(app.Urls.First().TrimEnd('/') + McpEndpoints.Route), shop, blog);
        }

        public T Use<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = _app.Services.CreateScope();
            return work(scope.ServiceProvider).GetAwaiter().GetResult();
        }

        public IssuedPersonalToken Issue(string scope = "read", params ProjectId[] projects) =>
            Use(s => s.GetRequiredService<PersonalTokenService>().IssueAsync(
                Ana, new PersonalTokenInput("Claude Code", scope, projects.Length == 0 ? null : [.. projects.Select(p => p.Value)], null), default));

        public IReadOnlyList<string> ToolNames(Role role) =>
            Use(s => Task.FromResult<IReadOnlyList<string>>([.. s.GetRequiredService<ToolRegistry>().For(role, ToolChannel.Mcp).Select(t => t.Name)]));

        public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class Session(McpClient client) : IDisposable
    {
        public IList<McpClientTool> Tools() => client.ListToolsAsync().AsTask().GetAwaiter().GetResult();

        public CallToolResult Call(string tool, ProjectId? project, Dictionary<string, object?>? args = null)
        {
            args ??= [];
            if (project is { } p)
            {
                args["project"] = p.Value.ToString();
            }

            return client.CallToolAsync(tool, args).AsTask().GetAwaiter().GetResult();
        }

        public void Dispose() => client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static Session Connect(Host host, string token) =>
        new(McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = host.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        })).GetAwaiter().GetResult());

    /// <summary>A raw initialize POST, to see the HTTP status before any MCP happens.</summary>
    private static HttpStatusCode RawStatus(Host host, Action<HttpRequestMessage> sign)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        sign(request);
        using var response = http.Send(request);
        return response.StatusCode;
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    public static void ListsOnlyRoleToolsWithoutScreenActions()
    {
        using var host = Host.Start();
        using var session = Connect(host, host.Issue("read_propose").Plain);
        var tools = session.Tools();
        var names = tools.Select(t => t.Name).ToHashSet();

        var expected = host.ToolNames(Role.Debugger).Union(host.ToolNames(Role.Monitor)).ToHashSet();
        Assert.True(names.SetEquals(expected), $"tools/list is the union of the roles' MCP tools: got {string.Join(",", names)}.");
        Assert.True(!names.Overlaps(ScreenTools), "No screen actions over MCP.");
        Assert.True(names.Contains("list_projects") && names.Contains("draft_decision") && names.Contains("security_findings"), "Role tools are there.");
        Assert.Equal(names.Count, tools.Count); // each tool once

        foreach (var tool in tools.Where(t => t.Name != "list_projects"))
        {
            var schema = tool.ProtocolTool.InputSchema;
            Assert.True(schema.GetProperty("properties").TryGetProperty("project", out var project) && project.GetProperty("type").GetString() == "string",
                $"{tool.Name} takes a project.");
            Assert.True(schema.GetProperty("required").EnumerateArray().Any(r => r.GetString() == "project"), $"{tool.Name} requires the project.");
        }

        // Only the Monitor project, through a limited token: no ViewCode tools, no drafting (Monitor has no ChatAct).
        using var monitor = Connect(host, host.Issue("read_propose", host.Blog).Plain);
        var monitorNames = monitor.Tools().Select(t => t.Name).ToHashSet();
        Assert.True(monitorNames.SetEquals(host.ToolNames(Role.Monitor).Where(n => n != "draft_decision")), "A Monitor-only token sees Monitor tools.");
        Assert.True(!monitorNames.Contains("security_findings") && !monitorNames.Contains("draft_decision"), "Nothing above the role.");

        var overview = session.Call("graph_overview", host.Shop);
        Assert.True(overview.IsError != true, $"A read tool runs: {TextOf(overview)}");
        Assert.True(JsonDocument.Parse(TextOf(overview)).RootElement.TryGetProperty("indexed", out _), "The result is the tool's JSON.");

        var screen = session.Call("focus_node", host.Shop, new() { ["nodeId"] = "x" });
        Assert.True(screen.IsError == true, "A screen action cannot be called over MCP.");
    }

    public static void ReadTokenHidesDraftDecision()
    {
        using var host = Host.Start();
        using var session = Connect(host, host.Issue("read").Plain);
        Assert.True(session.Tools().All(t => t.Name != "draft_decision"), "A read token does not list draft_decision.");

        var draft = session.Call("draft_decision", host.Shop, new()
        {
            ["area"] = "Stack", ["title"] = "Pick a cache", ["context"] = "Why", ["recommendedKey"] = "a",
            ["options"] = new[] { new { key = "a", title = "A", tradeOffs = "x" }, new { key = "b", title = "B", tradeOffs = "y" } },
        });
        Assert.True(draft.IsError == true, "A read token cannot call draft_decision.");
        var decisions = host.Use(s => s.GetRequiredService<IDecisionRepository>().ListAsync(host.Shop, default));
        Assert.Equal(0, decisions.Count);
    }

    public static void ReadProposeTokenDraftsAsProposed()
    {
        using var host = Host.Start();
        using var session = Connect(host, host.Issue("read_propose").Plain);
        var draft = session.Call("draft_decision", host.Shop, new()
        {
            ["area"] = "Stack", ["title"] = "Pick a cache", ["context"] = "Why", ["recommendedKey"] = "a",
            ["options"] = new[] { new { key = "a", title = "A", tradeOffs = "x" }, new { key = "b", title = "B", tradeOffs = "y" } },
        });
        Assert.True(draft.IsError != true, $"A read_propose Debugger drafts: {TextOf(draft)}");
        var decisions = host.Use(s => s.GetRequiredService<IDecisionRepository>().ListAsync(host.Shop, default));
        Assert.Equal(Domain.Decisions.DecisionStatus.Proposed, decisions.Single().Status);

        var onBlog = session.Call("draft_decision", host.Blog, new()
        {
            ["area"] = "Stack", ["title"] = "Pick a cache", ["context"] = "Why", ["recommendedKey"] = "a",
            ["options"] = new[] { new { key = "a", title = "A", tradeOffs = "x" }, new { key = "b", title = "B", tradeOffs = "y" } },
        });
        Assert.True(onBlog.IsError == true, "Not where the role has no ChatAct (Monitor).");
    }

    public static void CallOnUnlistedProjectRefused()
    {
        using var host = Host.Start();
        using var session = Connect(host, host.Issue("read", host.Shop).Plain);
        Assert.True(session.Call("graph_overview", host.Shop).IsError != true, "Its own project works.");

        var refused = session.Call("graph_overview", host.Blog);
        Assert.True(refused.IsError == true, "A project the token is not limited to is refused.");
        Assert.True(TextOf(refused).Contains("not found", StringComparison.Ordinal), $"Plain message: {TextOf(refused)}");

        Assert.True(session.Call("graph_overview", ProjectId.New()).IsError == true, "An unknown project is refused.");
        Assert.True(session.Call("graph_overview", null).IsError == true, "A missing project is refused.");

        var listed = JsonDocument.Parse(TextOf(session.Call("list_projects", null))).RootElement.GetProperty("projects");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal(host.Shop.Value, listed[0].GetProperty("id").GetGuid());
    }

    public static void MemberRemovedAfterTokenCreationRefused()
    {
        using var host = Host.Start();
        using var session = Connect(host, host.Issue("read").Plain);
        Assert.True(session.Call("graph_overview", host.Blog).IsError != true, "Works while a member.");

        host.Use(async s =>
        {
            var repo = s.GetRequiredService<IProjectRepository>();
            var blog = (await repo.GetAsync(host.Blog, default))!;
            blog.RemoveMember(Ana, Owner, Now);
            await repo.SaveAsync(blog, default);
            return true;
        });

        Assert.True(session.Call("graph_overview", host.Blog).IsError == true, "Membership is checked on every call.");
        Assert.True(session.Call("graph_overview", host.Shop).IsError != true, "The other project still works.");
        Assert.True(session.Tools().Select(t => t.Name).ToHashSet().SetEquals(host.ToolNames(Role.Debugger).Where(n => n != "draft_decision")), "The list follows real membership.");
    }

    public static void RevokedTokenGets401()
    {
        using var host = Host.Start();
        var issued = host.Issue();
        Assert.Equal(HttpStatusCode.OK, RawStatus(host, r => r.Headers.Authorization = new("Bearer", issued.Plain)));

        host.Use(async s =>
        {
            await s.GetRequiredService<PersonalTokenService>().RevokeAsync(Ana, new PersonalTokenId(issued.Token.Id), default);
            return true;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, RawStatus(host, r => r.Headers.Authorization = new("Bearer", issued.Plain)));
    }

    public static void ExpiredTokenGets401()
    {
        using var host = Host.Start();
        var issued = host.Issue();
        host.Clock.Now = Now.AddDays(90);
        Assert.Equal(HttpStatusCode.Unauthorized, RawStatus(host, r => r.Headers.Authorization = new("Bearer", issued.Plain)));
    }

    public static void DevHeaderAloneCannotReachMcp()
    {
        using var host = Host.Start();
        Assert.Equal(HttpStatusCode.Unauthorized, RawStatus(host, r => r.Headers.Add(UserAuthentication.DevHeader, Ana.Value)));
        Assert.Equal(HttpStatusCode.Unauthorized, RawStatus(host, _ => { }));
        Assert.Equal(HttpStatusCode.Unauthorized, RawStatus(host, r => r.Headers.Authorization = new("Bearer", "mpat_nope_nope")));
    }
}
