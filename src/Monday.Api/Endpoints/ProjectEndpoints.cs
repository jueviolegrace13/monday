using System.Security.Claims;
using Monday.Api.Security;
using Monday.Application.Activity;
using Monday.Application.CodeGraph;
using Monday.Application.Decisions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Decisions;
using Monday.Domain.Projects;

namespace Monday.Api.Endpoints;

public sealed record CreateProjectRequest(string Name);

public sealed record AddEnvironmentRequest(string Name, EnvironmentKind Kind);

public sealed record SetKindRequest(EnvironmentKind Kind);

public sealed record RotateKeyRequest(int OverlapHours);

public sealed record SetMemberRequest(Role Role);

public sealed record ApproveRequest(string OptionKey, string? Note);

public sealed record RejectRequest(string Note);

public sealed record DecisionView(
    Guid Id, DecisionArea Area, string Title, string Context, IReadOnlyList<DecisionOption> Options, string RecommendedKey,
    Guid? Supersedes, DecisionStatus Status, string? ChosenKey, string? ResolvedBy, DateTimeOffset ProposedAt, DateTimeOffset? ResolvedAt, string? Note,
    string? DraftedBy)
{
    public static DecisionView Of(Decision d) => new(
        d.Id.Value, d.Area, d.Title, d.Context, d.Options, d.RecommendedKey, d.Supersedes?.Value, d.Status, d.ChosenKey,
        d.ResolvedBy?.Value, d.ProposedAt, d.ResolvedAt, d.Note, d.DraftedBy?.Value);
}

/// <summary>What signed-in people do. Every call is checked against their role on that project.</summary>
public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/projects").WithTags("Projects").RequireAuthorization();

        projects.MapGet("/", ListProjects);
        projects.MapPost("/", CreateProject);
        projects.MapPost("/{id:guid}/environments", AddEnvironment);
        projects.MapPut("/{id:guid}/environments/{environmentId:guid}/kind", SetEnvironmentKind);

        projects.MapGet("/{id:guid}/keys", ListKeys);
        projects.MapPost("/{id:guid}/environments/{environmentId:guid}/keys", IssueKey);
        projects.MapPost("/{id:guid}/keys/{keyId:guid}/rotate", RotateKey);
        projects.MapDelete("/{id:guid}/keys/{keyId:guid}", RevokeKey);

        projects.MapPut("/{id:guid}/members/{member}", SetMember);

        projects.MapGet("/{id:guid}/graph", GetGraph);
        projects.MapGet("/{id:guid}/graph/node", GetNode);
        projects.MapGet("/{id:guid}/activity", GetActivity);

        projects.MapGet("/{id:guid}/decisions", ListDecisions);
        projects.MapPost("/{id:guid}/decisions/{decisionId:guid}/approve", Approve);
        projects.MapPost("/{id:guid}/decisions/{decisionId:guid}/reject", Reject);
        return app;
    }

    private static async Task<IResult> ListProjects(ClaimsPrincipal user, ProjectService projects, CancellationToken ct) =>
        Results.Ok(await projects.ListAsync(user.UserIdOf(), ct));

    private static async Task<IResult> CreateProject(CreateProjectRequest request, ClaimsPrincipal user, ProjectService projects, CancellationToken ct)
    {
        var created = await projects.CreateAsync(request.Name, user.UserIdOf(), ct);
        return Results.Created($"/projects/{created.Id}", created);
    }

    private static async Task<IResult> AddEnvironment(Guid id, AddEnvironmentRequest request, ClaimsPrincipal user, ProjectService projects, CancellationToken ct) =>
        Results.Ok(await projects.AddEnvironmentAsync(new ProjectId(id), request.Name, request.Kind, user.UserIdOf(), ct));

    private static async Task<IResult> SetEnvironmentKind(Guid id, Guid environmentId, SetKindRequest request, ClaimsPrincipal user, ProjectService projects, CancellationToken ct)
    {
        await projects.SetEnvironmentKindAsync(new ProjectId(id), new EnvironmentId(environmentId), request.Kind, user.UserIdOf(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListKeys(Guid id, ClaimsPrincipal user, ProjectService projects, CancellationToken ct) =>
        Results.Ok(await projects.ListKeysAsync(new ProjectId(id), user.UserIdOf(), ct));

    /// <summary>The plain key is in this one response only.</summary>
    private static async Task<IResult> IssueKey(Guid id, Guid environmentId, ClaimsPrincipal user, ProjectService projects, CancellationToken ct) =>
        Results.Ok(new { key = await projects.IssueKeyAsync(new ProjectId(id), new EnvironmentId(environmentId), user.UserIdOf(), ct) });

    private static async Task<IResult> RotateKey(Guid id, Guid keyId, RotateKeyRequest request, ClaimsPrincipal user, ProjectService projects, CancellationToken ct) =>
        Results.Ok(new { key = await projects.RotateKeyAsync(new ProjectId(id), new ApiKeyId(keyId), TimeSpan.FromHours(request.OverlapHours), user.UserIdOf(), ct) });

    private static async Task<IResult> RevokeKey(Guid id, Guid keyId, ClaimsPrincipal user, ProjectService projects, CancellationToken ct)
    {
        await projects.RevokeKeyAsync(new ProjectId(id), new ApiKeyId(keyId), user.UserIdOf(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetMember(Guid id, string member, SetMemberRequest request, ClaimsPrincipal user, ProjectService projects, CancellationToken ct)
    {
        await projects.SetMemberAsync(new ProjectId(id), new UserId(member), request.Role, user.UserIdOf(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetGraph(Guid id, ClaimsPrincipal user, GraphService graphs, CancellationToken ct) =>
        await graphs.GetAsync(new ProjectId(id), user.UserIdOf(), ct) is { } view ? Results.Ok(view) : Results.NoContent();

    private static async Task<IResult> GetNode(Guid id, string nodeId, ClaimsPrincipal user, GraphService graphs, CancellationToken ct) =>
        Results.Ok(await graphs.GetNodeAsync(new ProjectId(id), nodeId, user.UserIdOf(), ct));

    private static async Task<IResult> GetActivity(Guid id, DateTimeOffset from, DateTimeOffset to, ClaimsPrincipal user, ActivityService activity, CancellationToken ct) =>
        Results.Ok(await activity.RangeAsync(new ProjectId(id), from, to, user.UserIdOf(), ct));

    private static async Task<IResult> ListDecisions(Guid id, ClaimsPrincipal user, DecisionService decisions, CancellationToken ct) =>
        Results.Ok((await decisions.ListAsync(new ProjectId(id), user.UserIdOf(), ct)).Select(DecisionView.Of));

    private static async Task<IResult> Approve(Guid id, Guid decisionId, ApproveRequest request, ClaimsPrincipal user, DecisionService decisions, CancellationToken ct) =>
        Results.Ok(DecisionView.Of(await decisions.ApproveAsync(new ProjectId(id), new DecisionId(decisionId), request.OptionKey, request.Note, user.UserIdOf(), ct)));

    private static async Task<IResult> Reject(Guid id, Guid decisionId, RejectRequest request, ClaimsPrincipal user, DecisionService decisions, CancellationToken ct) =>
        Results.Ok(DecisionView.Of(await decisions.RejectAsync(new ProjectId(id), new DecisionId(decisionId), request.Note, user.UserIdOf(), ct)));
}
