using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Monday.Application.Access;
using Monday.Domain.Access;
using Monday.Domain.Projects;

namespace Monday.Api.Security;

/// <summary>
/// How outside agents sign in to the MCP endpoint: <c>Authorization: Bearer mpat_…</c>. The scheme is registered but is
/// not a default, so it only runs where an endpoint names it (<c>/mcp</c>). A token never signs in to any other endpoint,
/// including the ones that manage tokens. Anything wrong with the token is the same plain 401.
/// </summary>
public static class PersonalTokenAuthentication
{
    public const string Scheme = "MondayToken";

    /// <summary>Claim holding the token's scope, <c>read</c> or <c>read_propose</c>.</summary>
    public const string ScopeClaim = "monday:token_scope";

    /// <summary>One claim per allowed project (its id), or a single <see cref="AllProjects"/> when the token has no limit.</summary>
    public const string ProjectClaim = "monday:token_project";

    public const string AllProjects = "*";

    /// <summary>The token's public prefix, used to rate-limit per token.</summary>
    public const string PrefixClaim = "monday:token_prefix";

    private const string BearerPrefix = "Bearer ";
    private const string ItemKey = "monday.personal_token";

    public static IServiceCollection AddMondayPersonalTokenAuthentication(this IServiceCollection services)
    {
        // No default scheme is set here: AddAuthentication() with no argument only adds the services.
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, null);
        return services;
    }

    /// <summary>The token behind a request that authenticated with <see cref="Scheme"/>.</summary>
    public static PersonalToken PersonalTokenOf(this HttpContext http) =>
        http.Items[ItemKey] as PersonalToken ?? throw new InvalidOperationException("Endpoint is not authenticated with a personal token.");

    /// <summary>Rate-limit partition: one bucket per token prefix, from the authenticated claim only (random prefixes can't mint buckets).</summary>
    public static string PartitionOf(HttpContext http) =>
        http.User.FindFirstValue(PrefixClaim) ?? "anonymous";

    private sealed class Handler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PersonalTokenService tokens)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(header))
            {
                return AuthenticateResult.NoResult();
            }

            // Fail without saying why: a wrong, revoked, expired or malformed token all look the same.
            var presented = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..].Trim() : null;
            var token = await tokens.AuthenticateAsync(presented, Context.RequestAborted);
            if (token is null)
            {
                return AuthenticateResult.Fail("Invalid personal access token.");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, token.UserId.Value),
                new(ScopeClaim, token.Scope.ToWire()),
                new(PrefixClaim, token.Prefix),
            };
            claims.AddRange(token.Projects is { } limited
                ? limited.Select(p => new Claim(ProjectClaim, p.Value.ToString()))
                : [new Claim(ProjectClaim, AllProjects)]);

            Context.Items[ItemKey] = token;
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, PersonalTokenAuthentication.Scheme)), PersonalTokenAuthentication.Scheme);
            return AuthenticateResult.Success(ticket);
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }
}
