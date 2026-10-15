using Monday.Api.Security;
using Monday.Application.Activity;
using Monday.Application.CodeGraph;
using Monday.Application.Decisions;

namespace Monday.Api.Endpoints;

/// <summary>
/// Where apps, their CI and Claude sessions send data. Every route here only receives:
/// none of them reaches back into an app, so production keys are safe to use.
/// </summary>
public static class IngestEndpoints
{
    public const string RateLimitPolicy = "ingest";

    public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder app)
    {
        var ingest = app.MapGroup("/ingest")
            .WithTags("Ingest")
            .RequireReporterKey()
            .RequireRateLimiting(RateLimitPolicy)
            .DisableAntiforgery();

        ingest.MapPost("/graph", IngestGraph);
        ingest.MapPost("/explanations", IngestExplanations);
        ingest.MapPost("/activity", IngestActivity);
        ingest.MapPost("/decisions", ProposeDecision);
        return app;
    }

    private static async Task<IResult> IngestGraph(GraphUpload upload, HttpContext http, GraphService graphs) =>
        Results.Ok(new { nodes = await graphs.IngestAsync(http.Reporter(), upload, http.RequestAborted) });

    private static async Task<IResult> IngestExplanations(IReadOnlyList<ExplanationUpload> uploads, HttpContext http, GraphService graphs) =>
        Results.Ok(new { explanations = await graphs.IngestExplanationsAsync(http.Reporter(), uploads, http.RequestAborted) });

    private static async Task<IResult> IngestActivity(IReadOnlyList<ActivityUpload> uploads, HttpContext http, ActivityService activity) =>
        Results.Ok(new { events = await activity.RecordAsync(http.Reporter(), uploads, http.RequestAborted) });

    private static async Task<IResult> ProposeDecision(DecisionProposal proposal, HttpContext http, DecisionService decisions)
    {
        var decision = await decisions.ProposeAsync(http.Reporter(), proposal, http.RequestAborted);
        return Results.Ok(new { id = decision.Id.Value, status = decision.Status });
    }
}
