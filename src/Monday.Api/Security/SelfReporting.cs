using Microsoft.Extensions.Options;
using Monday.Agent;
using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Application.Traffic;
using Monday.Domain.Common;

namespace Monday.Api.Security;

/// <summary>
/// Monday watching itself: its own agent hands requests straight to the traffic use case, with no
/// HTTP hop and no request to /ingest/traffic that would itself be recorded.
/// </summary>
public sealed class SelfTraceSink(IServiceScopeFactory scopes, IOptions<MondayAgentOptions> options) : ITraceSink
{
    public async Task SendAsync(IReadOnlyList<AgentTrace> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using var scope = scopes.CreateAsyncScope();
        var reporter = await scope.ServiceProvider.GetRequiredService<ApiKeyAuthenticator>().AuthenticateAsync(options.Value.ApiKey, ct)
            ?? throw new HttpRequestException("Monday's own API key is not valid.");
        var uploads = batch.Select(t => new TrafficUpload(t.TraceId, t.At, t.Method, t.Route, t.Status, t.DurationMs, t.Guards, t.UserHash, t.ErrorType)).ToList();
        await scope.ServiceProvider.GetRequiredService<TrafficService>().IngestAsync(reporter, uploads, ct);
    }
}

/// <summary>
/// Development only: creates the "Monday" project for the configured owner and points Monday's own
/// agent at it, so a local run watches itself from the first request.
/// </summary>
public static partial class DevBootstrap
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Development: project Monday {ProjectId} created for {Owner}; Monday reports its own traffic to it.")]
    private static partial void LogBootstrapped(ILogger logger, Guid projectId, string owner);

    public static async Task RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var owner = app.Configuration["Monday:Bootstrap:Owner"];
        if (!app.Environment.IsDevelopment() || string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var user = new UserId(owner);
        var project = await projects.CreateAsync("Monday", user, CancellationToken.None);
        var key = await projects.IssueKeyAsync(new Domain.Projects.ProjectId(project.Id), new Domain.Projects.EnvironmentId(project.Environments[0].Id), user, CancellationToken.None);
        var hashKey = await projects.RevealUserHashKeyAsync(new Domain.Projects.ProjectId(project.Id), user, CancellationToken.None);

        var agent = app.Services.GetRequiredService<IOptions<MondayAgentOptions>>().Value;
        agent.ApiKey = key;
        agent.UserHashKey = hashKey;
        agent.Endpoint ??= new Uri("http://self/");
        LogBootstrapped(app.Logger, project.Id, owner);
    }
}
