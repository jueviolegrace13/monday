using Monday.Domain.Common;
using Monday.Domain.Projects;
using Monday.Domain.Traffic;

namespace Monday.Domain.Tests;

internal static class TrafficTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);
    private const string Trace = "0123456789abcdef0123456789abcdef";

    private static RequestTrace Req(string route = "/projects/{id:guid}", int status = 200, double ms = 12, string? user = null, string? error = null) =>
        RequestTrace.Record(ProjectId.New(), EnvironmentId.New(), Trace, Now, "get", route, status, ms, ["RequireAuthorization"], user, error);

    public static void RecordsOnlyHowARequestMoved()
    {
        var r = Req(status: 429);
        Assert.Equal("GET", r.Method);
        Assert.Equal(Outcome.RateLimited, r.Outcome);
        Assert.Equal("endpoint:GET /projects/{id:guid}", r.GraphNodeId);
    }

    public static void RefusesConcreteUrlsQueriesNamesAndMessages()
    {
        Assert.Throws<DomainException>(() => Req(route: "/search?q=alice"), "traffic.route_not_template");
        Assert.Throws<DomainException>(() => Req(route: "projects/1"), "traffic.route_not_template");
        Assert.Throws<DomainException>(() => Req(user: "alice@example.com"), "traffic.user_hash");
        Assert.Throws<DomainException>(() => Req(error: "SqlException: login failed for user 'sa'"), "traffic.error_type");
    }

    public static void UserHashIsStablePerProjectAndDifferentAcrossProjects()
    {
        var owner = new UserId("github:rendee");
        var a = Project.Create("A", owner, Now);
        var b = Project.Create("B", owner, Now);
        Assert.Equal(a.HashUser("user-42"), a.HashUser(" user-42 "));
        Assert.True(a.HashUser("user-42") != b.HashUser("user-42"), "Same user hashes differently in another project.");
        Assert.True(!a.HashUser("user-42").Contains("42", StringComparison.Ordinal) || a.HashUser("user-42").Length == 32, "Hash is 32 hex.");
        Assert.Throws<DomainException>(() => a.RevealUserHashKey(new UserId("github:ana"), Now), "access.owner_only");
    }

    public static void StatsAndRetention()
    {
        var traces = Enumerable.Range(1, 100).Select(i => Req(ms: i, status: i % 20 == 0 ? 500 : 200)).ToList();
        var stats = TrafficStats.Of(traces, TimeSpan.FromSeconds(10));
        Assert.Equal(10.0, stats.PerSecond);
        Assert.Equal(96.0, stats.P95Ms);
        Assert.Equal(0.05, stats.ErrorRate);
        Assert.True(TrafficRetention.BelongsInArchive(traces[0], Now.AddDays(8)), "Older than 7 days goes to the archive.");
        Assert.True(!TrafficRetention.BelongsInArchive(traces[0], Now.AddDays(6)), "Within 7 days stays live.");
    }
}
