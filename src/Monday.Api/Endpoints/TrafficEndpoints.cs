using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Monday.Api.Security;
using Monday.Application.Projects;
using Monday.Application.Traffic;
using Monday.Domain.Projects;

namespace Monday.Api.Endpoints;

/// <summary>Live traffic, totals and replay for people on a project. All read-only.</summary>
public static class TrafficEndpoints
{
    public static IEndpointRouteBuilder MapTraffic(this IEndpointRouteBuilder app)
    {
        var traffic = app.MapGroup("/projects/{id:guid}/traffic").WithTags("Traffic").RequireAuthorization();
        traffic.MapGet("/stats", Stats);
        traffic.MapGet("/", Replay);
        traffic.MapGet("/live", Live);

        app.MapPost("/projects/{id:guid}/user-hash-key", RevealKey).WithTags("Projects").RequireAuthorization();
        return app;
    }

    private static async Task<IResult> Stats(Guid id, int? seconds, ClaimsPrincipal user, TrafficService traffic, CancellationToken ct) =>
        Results.Ok(await traffic.StatsAsync(new ProjectId(id), TimeSpan.FromSeconds(Math.Clamp(seconds ?? 60, 1, 604_800)), user.UserIdOf(), ct));

    private static async Task<IResult> Replay(Guid id, DateTimeOffset since, DateTimeOffset until, string? userId, ClaimsPrincipal user, TrafficService traffic, CancellationToken ct) =>
        Results.Ok(await traffic.ReplayAsync(new ProjectId(id), since, until, userId, user.UserIdOf(), ct));

    /// <summary>Server-sent events: one "request" event per request as it happens.</summary>
    private static async Task Live(Guid id, HttpContext http, ClaimsPrincipal user, TrafficService traffic, IOptions<JsonOptions> json)
    {
        var ct = http.RequestAborted;
        var stream = await traffic.LiveAsync(new ProjectId(id), user.UserIdOf(), ct);
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
        try
        {
            await foreach (var view in stream.WithCancellation(ct))
            {
                await http.Response.WriteAsync($"event: request\ndata: {JsonSerializer.Serialize(view, json.Value.SerializerOptions)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The viewer left.
        }
    }

    private static async Task<IResult> RevealKey(Guid id, ClaimsPrincipal user, ProjectService projects, CancellationToken ct) =>
        Results.Ok(new { userHashKey = await projects.RevealUserHashKeyAsync(new ProjectId(id), user.UserIdOf(), ct) });
}
