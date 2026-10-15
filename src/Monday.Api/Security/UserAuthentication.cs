using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Monday.Domain.Common;

namespace Monday.Api.Security;

/// <summary>
/// How people sign in. GitHub sign-in (approved 2026-10-15) arrives with the viewer in phase 2.
/// Until then, Development accepts an <c>X-Monday-Dev-User</c> header so the API can be exercised
/// locally. That scheme is never registered outside Development: there, every user call is refused.
/// </summary>
public static class UserAuthentication
{
    public const string DevScheme = "MondayDevUser";
    public const string ClosedScheme = "MondayClosed";
    public const string DevHeader = "X-Monday-Dev-User";

    public static IServiceCollection AddMondayUserAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        var scheme = environment.IsDevelopment() ? DevScheme : ClosedScheme;
        services.AddAuthentication(scheme)
            .AddScheme<AuthenticationSchemeOptions, DevUserHandler>(DevScheme, null)
            .AddScheme<AuthenticationSchemeOptions, ClosedHandler>(ClosedScheme, null);
        services.AddAuthorization();
        return services;
    }

    public static UserId UserIdOf(this ClaimsPrincipal principal) =>
        new(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Signed-in user has no id."));

    private sealed class DevUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IHostEnvironment environment)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!environment.IsDevelopment())
            {
                return Task.FromResult(AuthenticateResult.Fail("Development sign-in is disabled outside Development."));
            }

            var user = Request.Headers[DevHeader].ToString();
            if (string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Trim())], DevScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), DevScheme)));
        }
    }

    private sealed class ClosedHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
