using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Decisions;
using Monday.Domain.Projects;

namespace Monday.Domain.Tests;

internal static class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 7, 0, 0, TimeSpan.Zero);
    private static readonly UserId Owner = new("github:rendee");
    private static readonly UserId Ana = new("github:ana");
    private static readonly UserId Ben = new("github:ben");

    public static void NewProjectHasOwnerAndDevelopmentEnvironment()
    {
        var project = Project.Create("Monday", Owner, Now);

        Assert.Equal(Role.Owner, project.RoleOf(Owner));
        Assert.Equal(EnvironmentKind.Development, project.Environments.Single().Kind);
    }

    public static void IssuedKeyAuthenticatesToItsEnvironmentAndIsStoredOnlyAsHash()
    {
        var project = Project.Create("Monday", Owner, Now);
        var dev = project.Environments.Single();

        var plain = project.IssueApiKey(dev.Id, Owner, Now);

        var match = project.Authenticate(plain, Now);
        Assert.True(match is not null && match.Value.Environment.Id == dev.Id, "Key should authenticate to its environment.");
        Assert.True(!project.ApiKeys.Single().Hash.Contains(plain, StringComparison.Ordinal), "Plain key must not be stored.");
        Assert.True(project.Authenticate(plain + "x", Now) is null, "A wrong key must not authenticate.");
        Assert.True(project.Authenticate("not-a-key", Now) is null, "Garbage must not authenticate.");
    }

    public static void RevokedKeyStopsWorkingAndRotatedKeyOverlaps()
    {
        var project = Project.Create("Monday", Owner, Now);
        var dev = project.Environments.Single();
        var oldPlain = project.IssueApiKey(dev.Id, Owner, Now);
        var oldKey = project.ApiKeys.Single();

        var newPlain = project.RotateApiKey(oldKey.Id, TimeSpan.FromHours(1), Owner, Now);

        Assert.True(project.Authenticate(oldPlain, Now.AddMinutes(30)) is not null, "Old key works during the overlap.");
        Assert.True(project.Authenticate(oldPlain, Now.AddHours(2)) is null, "Old key stops after the overlap.");
        Assert.True(project.Authenticate(newPlain, Now.AddHours(2)) is not null, "New key keeps working.");

        project.RevokeApiKey(project.ApiKeys[^1].Id, Owner, Now.AddHours(3));
        Assert.True(project.Authenticate(newPlain, Now.AddHours(3)) is null, "Revoked key stops at once.");
    }

    public static void MonitorCannotManageKeysOrSeeCode()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Monitor, Owner, Now);

        Assert.True(project.Can(Ana, Permission.ViewLiveGraph), "Monitor sees the live graph.");
        Assert.True(!project.Can(Ana, Permission.ViewCode), "Monitor does not see code.");
        Assert.Throws<AccessDeniedException>(() => project.IssueApiKey(project.Environments[0].Id, Ana, Now), "access.denied");
    }

    public static void DebuggerCanReplayUsersButNotRestoreArchive()
    {
        Assert.True(RolePermissions.Allows(Role.Debugger, Permission.ReplayUserTraffic), "Debugger replays user traffic.");
        Assert.True(!RolePermissions.Allows(Role.Debugger, Permission.RestoreArchive), "Debugger cannot restore archives.");
        Assert.True(RolePermissions.IsAudited(Permission.ReplayUserTraffic), "User-session replay is audited.");
    }

    public static void OnlyOwnerApprovesAndProjectKeepsAnOwner()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Admin, Owner, Now);

        Assert.True(!project.Can(Ana, Permission.ApproveDecisions), "Admin cannot approve decisions.");
        Assert.Throws<DomainException>(() => project.SetMember(Ben, Role.Owner, Ana, Now), "member.owner_only");
        Assert.Throws<DomainException>(() => project.RemoveMember(Owner, Owner, Now), "member.last_owner");
    }

    public static void OnlyOwnerMarksProduction()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Admin, Owner, Now);

        Assert.Throws<DomainException>(() => project.AddEnvironment("production", EnvironmentKind.Production, Ana), "access.owner_only");
        var prod = project.AddEnvironment("production", EnvironmentKind.Production, Owner);
        Assert.True(prod.IsReadOnly, "Production is read-only.");
        Assert.Throws<DomainException>(() => project.ChangeEnvironmentKind(prod.Id, EnvironmentKind.Development, Ana, Now), "access.owner_only");
    }

    public static void ProductionRefusesEveryWriteAction()
    {
        var project = Project.Create("Monday", Owner, Now);
        var prod = project.AddEnvironment("production", EnvironmentKind.Production, Owner);
        var dev = project.Environments[0];

        foreach (var action in Enum.GetValues<WriteAction>())
        {
            Assert.Throws<ProductionReadOnlyException>(() => ProductionGuard.EnsureWritable(prod, action), "production.read_only");
            ProductionGuard.EnsureWritable(dev, action);
        }
    }

    public static void DecisionNeedsOwnerAndNeverChangesOnceResolved()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Admin, Owner, Now);
        var decision = Decision.Propose(
            project.Id, DecisionArea.Stack, "Live store", "Where traffic lives for 7 days.",
            [new("neon", "Postgres on Neon", "Free; storage-capped"), new("sqlite", "SQLite on disk", "Lost on Render redeploy")],
            "neon", Now);

        Assert.Throws<AccessDeniedException>(() => decision.Approve("neon", project, Ana, Now), "access.denied");
        decision.Approve("neon", project, Owner, Now);
        Assert.Equal(DecisionStatus.Approved, decision.Status);
        Assert.Throws<DomainException>(() => decision.Reject(project, Owner, Now, "changed my mind"), "decision.already_resolved");
    }
}
