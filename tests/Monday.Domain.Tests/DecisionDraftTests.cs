using Monday.Api.Endpoints;
using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Decisions;
using Monday.Domain.Projects;

namespace Monday.Domain.Tests;

internal static class DecisionDraftTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly UserId Owner = new("github:rendee");
    private static readonly UserId Ana = new("github:ana");

    private static Decision DraftBy(Project project, UserId by) =>
        Decision.Draft(
            project.Id, DecisionArea.Stack, "Live store", "Where traffic lives for 7 days.",
            [new("neon", "Postgres on Neon", "Free; storage-capped"), new("sqlite", "SQLite on disk", "Lost on Render redeploy")],
            "neon", by, Now);

    public static void DraftRecordsWhoAsked()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Debugger, Owner, Now);

        var decision = DraftBy(project, Ana);

        Assert.Equal(Ana, decision.DraftedBy);
        Assert.Equal(DecisionStatus.Proposed, decision.Status);
    }

    public static void DrafterWhoIsNotOwnerCannotApproveOwnDraft()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Admin, Owner, Now);
        var decision = DraftBy(project, Ana);

        Assert.Throws<AccessDeniedException>(() => decision.Approve("neon", project, Ana, Now), "access.denied");
        Assert.Throws<AccessDeniedException>(() => decision.Reject(project, Ana, Now, "no"), "access.denied");
        decision.Approve("neon", project, Owner, Now);
        Assert.Equal(DecisionStatus.Approved, decision.Status);
    }

    public static void DecisionViewShowsWhoDraftedIt()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Debugger, Owner, Now);

        Assert.Equal("github:ana", DecisionView.Of(DraftBy(project, Ana)).DraftedBy);

        var proposed = Decision.Propose(project.Id, DecisionArea.Stack, "Live store", "Why.", [new("a", "A", "x"), new("b", "B", "y")], "a", Now);
        Assert.True(DecisionView.Of(proposed).DraftedBy is null, "A session's proposal has no drafter.");
    }
}
