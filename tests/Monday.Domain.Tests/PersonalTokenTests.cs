using Monday.Application.Access;
using Monday.Application.Agent;
using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Projects;
using Monday.Infrastructure.InMemory;

namespace Monday.Domain.Tests;

/// <summary>Personal access tokens: scope, project limits, expiry, revocation, hashing, audit and authentication.</summary>
internal static class PersonalTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly UserId Ana = new("github:ana");

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset Now { get; set; } = PersonalTokenTests.Now;
    }

    private static (PersonalToken Token, string Plain) Issue(
        TokenScope scope = TokenScope.Read, IReadOnlyList<ProjectId>? projects = null, TimeSpan? lifetime = null, string name = "Cursor") =>
        PersonalToken.Issue(Ana, name, scope, projects, lifetime, Now);

    public static void DefaultExpiryIsNinetyDays()
    {
        var (token, _) = Issue();
        Assert.Equal(Now.AddDays(90), token.ExpiresAt);
        Assert.True(token.IsActive(Now.AddDays(89)), "Still active on day 89.");
        Assert.True(!token.IsActive(Now.AddDays(90)), "Expired on day 90.");

        Assert.Equal(Now.AddDays(365), Issue(lifetime: TimeSpan.FromDays(365)).Token.ExpiresAt);
    }

    public static void LifetimeOverOneYearRefused()
    {
        Assert.Throws<DomainException>(() => Issue(lifetime: TimeSpan.FromDays(366)), "token.lifetime_max");
        Assert.Throws<DomainException>(() => Issue(lifetime: TimeSpan.Zero), "token.lifetime_invalid");
        Assert.Throws<DomainException>(() => Issue(lifetime: TimeSpan.FromDays(-1)), "token.lifetime_invalid");
    }

    public static void NameIsRequiredAndAtMostSixtyCharacters()
    {
        Assert.Throws<DomainException>(() => Issue(name: "  "), "token.name_required");
        Assert.Throws<DomainException>(() => Issue(name: new string('x', 61)), "token.name_required");
        Assert.Equal(60, Issue(name: new string('x', 60)).Token.Name.Length);
        Assert.Throws<DomainException>(() => Issue(projects: []), "token.projects_empty");
    }

    public static void ReadScopeCannotPropose()
    {
        var project = ProjectId.New();
        var read = Issue(TokenScope.Read).Token;
        var propose = Issue(TokenScope.ReadPropose).Token;

        Assert.True(read.Allows(project, ToolKind.Read), "A read token reads.");
        Assert.True(!read.Allows(project, ToolKind.Proposal), "A read token cannot propose.");
        Assert.True(propose.Allows(project, ToolKind.Read), "A read_propose token reads.");
        Assert.True(propose.Allows(project, ToolKind.Proposal), "A read_propose token proposes.");
        Assert.True(!propose.Allows(project, ToolKind.ScreenAction), "No token runs screen actions.");
    }

    public static void ProjectLimitRespected()
    {
        var (a, b) = (ProjectId.New(), ProjectId.New());
        var limited = Issue(TokenScope.ReadPropose, [a]).Token;
        Assert.True(limited.Allows(a, ToolKind.Proposal), "Its own project is allowed.");
        Assert.True(!limited.Allows(b, ToolKind.Read), "Another project is not.");
        Assert.True(!limited.Allows(b, ToolKind.Proposal), "Nor to propose on it.");

        Assert.True(Issue().Token.Allows(b, ToolKind.Read), "With no list, any project the user belongs to.");
    }

    public static void RevokedTokenInactive()
    {
        var (token, plain) = Issue();
        Assert.True(token.Matches(plain, Now), "Matches while active.");
        token.Revoke(Now.AddDays(1));
        Assert.True(!token.IsActive(Now.AddDays(1)), "Revoked is inactive.");
        Assert.True(!token.Matches(plain, Now.AddDays(1)), "A revoked token no longer matches.");
        Assert.Throws<DomainException>(() => token.Revoke(Now.AddDays(2)), "token.already_revoked");
    }

    public static void ExpiredTokenInactive()
    {
        var (token, plain) = Issue(lifetime: TimeSpan.FromDays(1));
        Assert.True(token.Matches(plain, Now.AddHours(23)), "Matches before expiry.");
        Assert.True(!token.Matches(plain, Now.AddDays(1)), "An expired token no longer matches.");
    }

    public static void PlainTokenShownOnceAndOnlyHashStored()
    {
        var (token, plain) = Issue();
        Assert.True(plain.StartsWith("mpat_", StringComparison.Ordinal), "Plain token starts with mpat_.");
        Assert.Equal(token.Prefix, PersonalToken.PrefixOf(plain)!);
        Assert.True(plain.Contains($"_{token.Prefix}_", StringComparison.Ordinal), "Format is mpat_<prefix>_<secret>.");
        Assert.True(token.Hash != plain && !token.Hash.Contains(plain[(plain.LastIndexOf('_') + 1)..], StringComparison.Ordinal), "The hash holds no secret.");
        Assert.Equal(64, token.Hash.Length);
        Assert.True(!token.ToString()!.Contains(plain, StringComparison.Ordinal), "The aggregate does not print the token.");

        Assert.True(token.Matches(plain, Now), "The right token matches.");
        Assert.True(!token.Matches(plain + "x", Now), "A changed secret does not.");
        Assert.True(!token.Matches($"mpat_{token.Prefix}_other", Now), "A guessed secret does not.");
        Assert.True(PersonalToken.PrefixOf("mon_abc_def") is null && PersonalToken.PrefixOf("mpat_only") is null && PersonalToken.PrefixOf(null) is null,
            "Only mpat_<prefix>_<secret> parses.");

        Assert.True(Issue().Plain != plain, "Each token is different.");
    }

    public static void TouchRecordsLastUsedAtMostOncePerMinute()
    {
        var (token, _) = Issue();
        Assert.True(token.Touch(Now), "The first use is recorded.");
        Assert.True(!token.Touch(Now.AddSeconds(59)), "A use within the minute is not.");
        Assert.Equal<DateTimeOffset?>(Now, token.LastUsedAt);
        Assert.True(token.Touch(Now.AddSeconds(60)), "A minute later it is.");
        Assert.Equal<DateTimeOffset?>(Now.AddSeconds(60), token.LastUsedAt);
    }

    // ---- service ----

    private sealed class World
    {
        public UserId Owner { get; } = new("github:rendee");
        public ProjectId Shop { get; private set; }
        public ProjectId Blog { get; private set; }
        public ProjectId Elsewhere { get; private set; }
        public MovableClock Clock { get; } = new();
        public InMemoryProjectRepository Projects { get; } = new();
        public InMemoryAuditLog Audit { get; } = new();
        public InMemoryPersonalTokenStore Store { get; } = new();
        public PersonalTokenService Service { get; private set; } = null!;

        public static World Build()
        {
            var w = new World();
            var projects = new ProjectService(w.Projects, w.Audit, w.Clock);
            var other = new UserId("github:other");
            w.Shop = new ProjectId(projects.CreateAsync("Shop", Ana, default).GetAwaiter().GetResult().Id);
            w.Blog = new ProjectId(projects.CreateAsync("Blog", Ana, default).GetAwaiter().GetResult().Id);
            w.Elsewhere = new ProjectId(projects.CreateAsync("Elsewhere", other, default).GetAwaiter().GetResult().Id);
            w.Service = new PersonalTokenService(w.Store, w.Projects, w.Audit, w.Clock);
            return w;
        }

        public IssuedPersonalToken Issue(string name = "Cursor", string scope = "read", IReadOnlyList<Guid>? projects = null, int? days = null) =>
            Service.IssueAsync(Ana, new PersonalTokenInput(name, scope, projects, days), default).GetAwaiter().GetResult();

        public IReadOnlyList<Domain.Audit.AuditEntry> AuditOf(ProjectId p) => Audit.ListAsync(p, default).GetAwaiter().GetResult();

        public PersonalToken? Authenticate(string? presented) => Service.AuthenticateAsync(presented, default).GetAwaiter().GetResult();
    }

    public static void ServiceStoresOnlyTheHashAndListNeverReturnsThePlainToken()
    {
        var w = World.Build();
        var issued = w.Issue();
        Assert.True(issued.Plain.StartsWith("mpat_", StringComparison.Ordinal), "Issue returns the plain token once.");

        var stored = w.Store.FindByPrefixAsync(PersonalToken.PrefixOf(issued.Plain)!, default).GetAwaiter().GetResult()!;
        Assert.True(stored.Hash != issued.Plain, "Only the hash is stored.");

        var list = w.Service.ListAsync(Ana, default).GetAwaiter().GetResult();
        var shown = System.Text.Json.JsonSerializer.Serialize(list) + issued.Token + issued;
        var secret = issued.Plain[(issued.Plain.LastIndexOf('_') + 1)..];
        Assert.True(!shown.Contains(secret, StringComparison.Ordinal) && !shown.Contains(stored.Hash, StringComparison.Ordinal),
            "The list, views and ToString hold neither the secret nor the hash.");
        Assert.Equal(1, list.Count);
        Assert.True(list[0].Active, "A new token is active.");
        Assert.Equal(0, w.Service.ListAsync(w.Owner, default).GetAwaiter().GetResult().Count);
    }

    public static void ProjectListMustBeProjectsTheUserBelongsTo()
    {
        var w = World.Build();
        Assert.Throws<NotFoundException>(() => w.Issue(projects: [w.Elsewhere.Value]));
        Assert.Throws<NotFoundException>(() => w.Issue(projects: [Guid.NewGuid()]));
        Assert.Throws<DomainException>(() => w.Issue(scope: "admin"), "token.scope_invalid");
        Assert.Throws<DomainException>(() => w.Issue(days: 400), "token.lifetime_max");
        Assert.Throws<DomainException>(() => w.Issue(days: int.MaxValue), "token.lifetime_max");
        Assert.Throws<DomainException>(() => w.Issue(days: 0), "token.lifetime_invalid");
        Assert.Equal(0, w.Service.ListAsync(Ana, default).GetAwaiter().GetResult().Count);

        var ok = w.Issue(projects: [w.Shop.Value]);
        Assert.Equal(w.Shop.Value, ok.Token.Projects![0]);
    }

    public static void IssueAndRevokeAuditEachReachableProject()
    {
        var w = World.Build();
        var all = w.Issue(name: "Everything");
        Assert.Equal(1, w.AuditOf(w.Shop).Count(a => a.Action == "token.issued"));
        Assert.Equal(1, w.AuditOf(w.Blog).Count(a => a.Action == "token.issued"));
        Assert.Equal(0, w.AuditOf(w.Elsewhere).Count(a => a.Action == "token.issued"));

        var limited = w.Issue(name: "Shop only", projects: [w.Shop.Value]);
        Assert.Equal(2, w.AuditOf(w.Shop).Count(a => a.Action == "token.issued"));
        Assert.Equal(1, w.AuditOf(w.Blog).Count(a => a.Action == "token.issued"));

        var entry = w.AuditOf(w.Shop).Last(a => a.Action == "token.issued");
        Assert.Equal(Ana, entry.Actor);
        Assert.Equal($"Shop only ({limited.Token.Prefix})", entry.Detail);

        w.Service.RevokeAsync(Ana, new PersonalTokenId(limited.Token.Id), default).GetAwaiter().GetResult();
        Assert.Equal(1, w.AuditOf(w.Shop).Count(a => a.Action == "token.revoked"));
        Assert.Equal(0, w.AuditOf(w.Blog).Count(a => a.Action == "token.revoked"));
        w.Service.RevokeAsync(Ana, new PersonalTokenId(all.Token.Id), default).GetAwaiter().GetResult();
        Assert.Equal(2, w.AuditOf(w.Shop).Count(a => a.Action == "token.revoked"));
        Assert.Equal(1, w.AuditOf(w.Blog).Count(a => a.Action == "token.revoked"));

        var everything = w.Audit.ListAsync(w.Shop, default).GetAwaiter().GetResult().Concat(w.AuditOf(w.Blog));
        var secret = all.Plain[(all.Plain.LastIndexOf('_') + 1)..];
        Assert.True(everything.All(a => !a.Detail.Contains(secret, StringComparison.Ordinal)), "The audit never holds the secret.");
    }

    public static void RepeatedProjectIdIsAuditedOnce()
    {
        var w = World.Build();
        var issued = w.Issue(projects: [w.Shop.Value, w.Shop.Value]);
        Assert.Equal(1, w.AuditOf(w.Shop).Count(a => a.Action == "token.issued"));

        w.Service.RevokeAsync(Ana, new PersonalTokenId(issued.Token.Id), default).GetAwaiter().GetResult();
        Assert.Equal(1, w.AuditOf(w.Shop).Count(a => a.Action == "token.revoked"));
    }

    public static void RevokeIsForTheOwnerOnly()
    {
        var w = World.Build();
        var issued = w.Issue();
        var id = new PersonalTokenId(issued.Token.Id);
        Assert.Throws<NotFoundException>(() => w.Service.RevokeAsync(w.Owner, id, default).GetAwaiter().GetResult());
        Assert.Throws<NotFoundException>(() => w.Service.RevokeAsync(Ana, PersonalTokenId.New(), default).GetAwaiter().GetResult());
        Assert.True(w.Authenticate(issued.Plain) is not null, "A refused revoke leaves the token working.");

        w.Service.RevokeAsync(Ana, id, default).GetAwaiter().GetResult();
        Assert.Throws<DomainException>(() => w.Service.RevokeAsync(Ana, id, default).GetAwaiter().GetResult(), "token.already_revoked");
    }

    public static void AuthenticationRejectsBadRevokedAndExpiredTokens()
    {
        var w = World.Build();
        var issued = w.Issue(days: 1);
        Assert.True(w.Authenticate(issued.Plain) is { } found && found.UserId == Ana, "A good token signs in as its user.");
        Assert.True(w.Authenticate(null) is null && w.Authenticate("") is null && w.Authenticate("Bearer x") is null, "Garbage is refused.");
        Assert.True(w.Authenticate($"mpat_{issued.Token.Prefix[5..]}_x") is null, "An unknown prefix is refused.");
        Assert.True(w.Authenticate(issued.Plain + "x") is null, "A wrong secret is refused.");
        Assert.True(w.Authenticate(issued.Plain.Replace("mpat_", "mon_", StringComparison.Ordinal)) is null, "Another key scheme is refused.");

        w.Clock.Now = Now.AddDays(1);
        Assert.True(w.Authenticate(issued.Plain) is null, "An expired token is refused.");

        w.Clock.Now = Now;
        var second = w.Issue();
        w.Service.RevokeAsync(Ana, new PersonalTokenId(second.Token.Id), default).GetAwaiter().GetResult();
        Assert.True(w.Authenticate(second.Plain) is null, "A revoked token is refused.");
        Assert.True(!w.Service.ListAsync(Ana, default).GetAwaiter().GetResult().Single(t => t.Id == second.Token.Id).Active, "The list shows it inactive.");
    }

    public static void AuthenticationRecordsLastUsedOncePerMinute()
    {
        var w = World.Build();
        var issued = w.Issue();
        DateTimeOffset? LastUsed() => w.Service.ListAsync(Ana, default).GetAwaiter().GetResult().Single().LastUsedAt;
        Assert.Equal<DateTimeOffset?>(null, LastUsed());

        w.Authenticate(issued.Plain);
        Assert.Equal<DateTimeOffset?>(Now, LastUsed());
        w.Clock.Now = Now.AddSeconds(30);
        w.Authenticate(issued.Plain);
        Assert.Equal<DateTimeOffset?>(Now, LastUsed());
        w.Clock.Now = Now.AddSeconds(61);
        w.Authenticate(issued.Plain);
        Assert.Equal<DateTimeOffset?>(Now.AddSeconds(61), LastUsed());
    }

    public static void UseRacingARevokeCannotUndoIt()
    {
        var w = World.Build();
        var issued = w.Issue();
        var stale = w.Authenticate(issued.Plain)!; // loaded before the revoke
        w.Service.RevokeAsync(Ana, new PersonalTokenId(issued.Token.Id), default).GetAwaiter().GetResult();

        w.Clock.Now = Now.AddMinutes(5);
        stale.Touch(w.Clock.Now);
        w.Store.SaveUsedAsync(stale, default).GetAwaiter().GetResult();
        Assert.True(w.Authenticate(issued.Plain) is null, "The late last-used save did not bring the token back.");
    }
}
