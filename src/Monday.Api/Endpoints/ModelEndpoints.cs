using System.Security.Claims;
using Monday.Api.Security;
using Monday.Application.Agent;
using Monday.Domain.Projects;

namespace Monday.Api.Endpoints;

/// <summary>A project's model settings. Owners and Admins only; the key goes in on PUT and never comes back out.</summary>
public static class ModelEndpoints
{
    public static IEndpointRouteBuilder MapModel(this IEndpointRouteBuilder app)
    {
        var model = app.MapGroup("/projects/{id:guid}/model").WithTags("Model").RequireAuthorization();
        model.MapGet("/", GetSettings);
        model.MapPut("/", SaveSettings);
        model.MapDelete("/", ClearSettings);
        model.MapPost("/test", TestConnection);
        model.MapGet("/usage", GetUsage);
        return app;
    }

    private static async Task<IResult> GetSettings(Guid id, ClaimsPrincipal user, ModelSettingsService settings, CancellationToken ct) =>
        Results.Ok(await settings.GetAsync(new ProjectId(id), user.UserIdOf(), ct));

    private static async Task<IResult> SaveSettings(Guid id, ModelSettingsInput input, ClaimsPrincipal user, ModelSettingsService settings, CancellationToken ct)
    {
        await settings.SaveAsync(new ProjectId(id), input, user.UserIdOf(), ct);
        return Results.Ok(await settings.GetAsync(new ProjectId(id), user.UserIdOf(), ct));
    }

    private static async Task<IResult> ClearSettings(Guid id, ClaimsPrincipal user, ModelSettingsService settings, CancellationToken ct)
    {
        await settings.ClearAsync(new ProjectId(id), user.UserIdOf(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestConnection(Guid id, ClaimsPrincipal user, ModelSettingsService settings, CancellationToken ct) =>
        Results.Ok(await settings.TestAsync(new ProjectId(id), user.UserIdOf(), ct));

    private static async Task<IResult> GetUsage(Guid id, ClaimsPrincipal user, ModelSettingsService settings, CancellationToken ct) =>
        Results.Ok(await settings.UsageAsync(new ProjectId(id), user.UserIdOf(), ct));
}
