using Monday.Domain.Projects;

namespace Monday.Application.Abstractions;

/// <summary>
/// An app (or its CI, or a Claude session working on it) that proved itself with an API key.
/// Reporters can only send data in; they never act on the app.
/// </summary>
public sealed record AppReporter(Project Project, ProjectEnvironment Environment);

/// <summary>Resolves a presented API key to the project and environment it belongs to.</summary>
public sealed class ApiKeyAuthenticator(IProjectRepository projects, IClock clock)
{
    public async Task<AppReporter?> AuthenticateAsync(string? presentedKey, CancellationToken ct)
    {
        var prefix = ApiKey.PrefixOf(presentedKey);
        if (prefix is null)
        {
            return null;
        }

        var project = await projects.FindByKeyPrefixAsync(prefix, ct).ConfigureAwait(false);
        var match = project?.Authenticate(presentedKey!, clock.Now);
        return match is null ? null : new AppReporter(project!, match.Value.Environment);
    }
}
