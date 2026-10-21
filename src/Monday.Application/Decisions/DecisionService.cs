using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Decisions;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Decisions;

public sealed record DecisionProposal(DecisionArea Area, string Title, string Context, IReadOnlyList<DecisionOption> Options, string RecommendedKey, Guid? Supersedes);

/// <summary>The approval gates: a Claude session proposes, the Owner decides.</summary>
public sealed class DecisionService(IDecisionRepository decisions, ProjectService projects, IClock clock)
{
    public async Task<Decision> ProposeAsync(AppReporter reporter, DecisionProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(proposal);
        var supersedes = proposal.Supersedes is { } s ? new DecisionId(s) : (DecisionId?)null;
        var decision = Decision.Propose(reporter.Project.Id, proposal.Area, proposal.Title, proposal.Context, proposal.Options, proposal.RecommendedKey, clock.Now, supersedes);
        await decisions.SaveAsync(decision, ct).ConfigureAwait(false);
        return decision;
    }

    public async Task<Decision> DraftAsync(ProjectId id, DecisionProposal proposal, UserId by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var project = await projects.LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        project.EnsureCan(by, Permission.ChatAct);
        var decision = Decision.Draft(id, proposal.Area, proposal.Title, proposal.Context, proposal.Options, proposal.RecommendedKey, by, clock.Now, proposal.Supersedes is { } s ? new DecisionId(s) : null);
        await decisions.SaveAsync(decision, ct).ConfigureAwait(false);
        return decision;
    }

    public async Task<IReadOnlyList<Decision>> ListAsync(ProjectId id, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ViewLiveGraph);
        return await decisions.ListAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<Decision> ApproveAsync(ProjectId id, DecisionId decisionId, string optionKey, string? note, UserId by, CancellationToken ct)
    {
        var (project, decision) = await LoadAsync(id, decisionId, by, ct).ConfigureAwait(false);
        decision.Approve(optionKey, project, by, clock.Now, note);
        await decisions.SaveAsync(decision, ct).ConfigureAwait(false);
        return decision;
    }

    public async Task<Decision> RejectAsync(ProjectId id, DecisionId decisionId, string note, UserId by, CancellationToken ct)
    {
        var (project, decision) = await LoadAsync(id, decisionId, by, ct).ConfigureAwait(false);
        decision.Reject(project, by, clock.Now, note);
        await decisions.SaveAsync(decision, ct).ConfigureAwait(false);
        return decision;
    }

    private async Task<(Project, Decision)> LoadAsync(ProjectId id, DecisionId decisionId, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        var decision = await decisions.GetAsync(decisionId, ct).ConfigureAwait(false);
        return decision is not null && decision.ProjectId == id ? (project, decision) : throw new NotFoundException("decision");
    }
}
