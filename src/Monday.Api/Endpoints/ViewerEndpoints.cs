namespace Monday.Api.Endpoints;

/// <summary>
/// The Angular viewer, built into <c>wwwroot</c>. Unknown paths serve its <c>index.html</c> so the app can route
/// itself, except the API's own prefixes and anything that looks like a file: those keep their 404.
/// </summary>
public static class ViewerEndpoints
{
    /// <summary>Everything the API serves; a miss under these is a real 404, never the viewer.</summary>
    private const string ApiPrefixes = "projects|ingest|health|me|mcp";

    /// <summary>The fallback's route template, which the agent leaves out of Monday's own traffic.</summary>
    public const string FallbackRoute = "/{*path:nonfile:regex(^(?!(" + ApiPrefixes + ")(/|$)))}";

    /// <summary>Must come after every API route: the fallback has the lowest priority, so it only sees what nothing else matched.</summary>
    public static IEndpointRouteBuilder MapViewer(this IEndpointRouteBuilder app)
    {
        app.MapFallbackToFile(FallbackRoute.TrimStart('/'), "index.html").WithMetadata(new HttpMethodMetadata(["GET", "HEAD"]));
        return app;
    }
}
