using System.Security.Claims;
using Monday.Api.Security;
using Monday.Application.Access;
using Monday.Domain.Access;

namespace Monday.Api.Endpoints;

/// <summary>
/// The signed-in person's own personal access tokens. These use the normal sign-in only, never a personal token.
/// The plain token comes back once, from POST; the list never has it.
/// </summary>
public static class TokenEndpoints
{
    public static IEndpointRouteBuilder MapTokens(this IEndpointRouteBuilder app)
    {
        var tokens = app.MapGroup("/me/tokens").WithTags("Tokens").RequireAuthorization();
        tokens.MapGet("/", List);
        tokens.MapPost("/", Issue);
        tokens.MapDelete("/{id:guid}", Revoke);
        return app;
    }

    private static async Task<IResult> List(ClaimsPrincipal user, PersonalTokenService service, CancellationToken ct) =>
        Results.Ok(await service.ListAsync(user.UserIdOf(), ct));

    private static async Task<IResult> Issue(PersonalTokenInput input, ClaimsPrincipal user, PersonalTokenService service, CancellationToken ct)
    {
        var issued = await service.IssueAsync(user.UserIdOf(), input, ct);
        return Results.Created($"/me/tokens/{issued.Token.Id}", new { issued.Token, Plain = issued.Plain });
    }

    private static async Task<IResult> Revoke(Guid id, ClaimsPrincipal user, PersonalTokenService service, CancellationToken ct)
    {
        await service.RevokeAsync(user.UserIdOf(), new PersonalTokenId(id), ct);
        return Results.NoContent();
    }
}
