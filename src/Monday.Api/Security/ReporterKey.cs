using Monday.Application.Abstractions;

namespace Monday.Api.Security;

/// <summary>
/// Guards the ingest endpoints: the caller must present a valid project API key in the
/// <c>X-Monday-Key</c> header. The resolved reporter is handed to the endpoint.
/// </summary>
public static class ReporterKey
{
    public const string Header = "X-Monday-Key";
    private const string ItemKey = "monday.reporter";

    public static RouteGroupBuilder RequireReporterKey(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var authenticator = http.RequestServices.GetRequiredService<ApiKeyAuthenticator>();
            var reporter = await authenticator.AuthenticateAsync(http.Request.Headers[Header].ToString(), http.RequestAborted);
            if (reporter is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "A valid project API key is required.");
            }

            http.Items[ItemKey] = reporter;
            return await next(context);
        });
        return group;
    }

    public static AppReporter Reporter(this HttpContext http) =>
        http.Items[ItemKey] as AppReporter ?? throw new InvalidOperationException("Endpoint is missing RequireReporterKey().");

    /// <summary>Rate-limit partition: one bucket per key prefix, so one noisy app can't starve others.</summary>
    public static string PartitionOf(HttpContext http) =>
        Monday.Domain.Projects.ApiKey.PrefixOf(http.Request.Headers[Header].ToString()) ?? "anonymous";
}
