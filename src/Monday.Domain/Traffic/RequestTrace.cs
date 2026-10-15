using System.Text.RegularExpressions;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Traffic;

/// <summary>Where in its path a request ended.</summary>
public enum Outcome
{
    /// <summary>Handled and answered (any status the handler chose).</summary>
    Handled,

    /// <summary>Stopped by authentication or authorization (401/403 before the handler).</summary>
    Unauthorized,

    /// <summary>Stopped by a rate limit (429).</summary>
    RateLimited,

    /// <summary>Rejected as invalid input (400/422).</summary>
    Invalid,

    /// <summary>No endpoint matched (404/405).</summary>
    NotFound,

    /// <summary>The app failed (5xx).</summary>
    Failed,
}

/// <summary>
/// One request, as Monday records it: how it moved, never what it carried. There is no field for a
/// body, query string, header, cookie, IP address or user name, so none can be stored. The route is
/// the template ("/projects/{id}"), never the concrete path, and the user is a keyed hash.
/// </summary>
public sealed partial record RequestTrace
{
    private RequestTrace()
    {
    }

    public ProjectId ProjectId { get; private init; }

    public EnvironmentId EnvironmentId { get; private init; }

    /// <summary>W3C trace id (32 hex), to link a request to its spans later.</summary>
    public string TraceId { get; private init; } = string.Empty;

    public DateTimeOffset At { get; private init; }

    public string Method { get; private init; } = string.Empty;

    /// <summary>Route template, e.g. "/projects/{id:guid}/keys".</summary>
    public string Route { get; private init; } = string.Empty;

    public int Status { get; private init; }

    public double DurationMs { get; private init; }

    public Outcome Outcome { get; private init; }

    /// <summary>Guards that applied, from the endpoint's metadata (e.g. "RequireAuthorization").</summary>
    public IReadOnlyList<string> Guards { get; private init; } = [];

    /// <summary>HMAC of the user id with the project's secret, or null for anonymous requests.</summary>
    public string? UserHash { get; private init; }

    /// <summary>Exception type name when the request failed; never its message.</summary>
    public string? ErrorType { get; private init; }

    public string GraphNodeId => $"endpoint:{Method} {Route}";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex TraceIdShape();

    [GeneratedRegex("^[0-9a-f]{16,64}$")]
    private static partial Regex HashShape();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$")]
    private static partial Regex TypeNameShape();

    public static RequestTrace Record(
        ProjectId projectId, EnvironmentId environmentId, string traceId, DateTimeOffset at, string method, string route,
        int status, double durationMs, IEnumerable<string>? guards, string? userHash, string? errorType)
    {
        Guard.That(traceId is not null && TraceIdShape().IsMatch(traceId), "traffic.trace_id", "Trace id must be 32 lowercase hex characters.");
        method = Guard.NotBlank(method, "traffic.method_required", "A request needs its HTTP method", 10).ToUpperInvariant();
        route = Guard.NotBlank(route, "traffic.route_required", "A request needs its route template", 300);

        // Privacy by construction: a route is a template, never a concrete path or a query.
        Guard.That(route.StartsWith('/') && !route.Contains('?', StringComparison.Ordinal) && !route.Contains('#', StringComparison.Ordinal),
            "traffic.route_not_template", "Send the route template (\"/items/{id}\"), not the requested URL.");
        Guard.That(status is >= 100 and <= 599, "traffic.status", "Status must be an HTTP status code.");
        Guard.That(durationMs is >= 0 and <= 600_000, "traffic.duration", "Duration must be between 0 and 10 minutes.");
        Guard.That(userHash is null || HashShape().IsMatch(userHash), "traffic.user_hash", "User must be a keyed hash (hex), never an id or name.");
        Guard.That(errorType is null || (errorType.Length <= 120 && TypeNameShape().IsMatch(errorType)), "traffic.error_type", "Send only the exception type name.");

        var cleanGuards = (guards ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.Ordinal).Take(20).ToList();
        Guard.That(cleanGuards.TrueForAll(g => g.Length <= 80), "traffic.guard", "Guard names are at most 80 characters.");

        return new RequestTrace
        {
            ProjectId = projectId,
            EnvironmentId = environmentId,
            TraceId = traceId!,
            At = at,
            Method = method,
            Route = route,
            Status = status,
            DurationMs = Math.Round(durationMs, 2),
            Outcome = OutcomeOf(status),
            Guards = cleanGuards,
            UserHash = userHash,
            ErrorType = errorType,
        };
    }

    public static Outcome OutcomeOf(int status) => status switch
    {
        401 or 403 => Outcome.Unauthorized,
        429 => Outcome.RateLimited,
        400 or 422 => Outcome.Invalid,
        404 or 405 => Outcome.NotFound,
        >= 500 => Outcome.Failed,
        _ => Outcome.Handled,
    };
}

/// <summary>How long traffic stays in the live store before it moves to the archive. Nothing is deleted.</summary>
public static class TrafficRetention
{
    public static readonly TimeSpan Live = TimeSpan.FromDays(7);

    public static bool BelongsInArchive(RequestTrace trace, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return now - trace.At > Live;
    }
}

/// <summary>Summary numbers for a window of traffic: what the header and alerts read.</summary>
public sealed record TrafficStats(int Requests, double PerSecond, double P50Ms, double P95Ms, double ErrorRate, IReadOnlyDictionary<Outcome, int> ByOutcome)
{
    public static TrafficStats Of(IReadOnlyCollection<RequestTrace> traces, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(traces);
        var latencies = traces.Select(t => t.DurationMs).Order().ToList();
        double Pct(double p) => latencies.Count == 0 ? 0 : latencies[Math.Min(latencies.Count - 1, (int)Math.Floor(latencies.Count * p))];
        var errors = traces.Count(t => t.Outcome == Outcome.Failed);
        return new TrafficStats(
            traces.Count,
            window.TotalSeconds > 0 ? Math.Round(traces.Count / window.TotalSeconds, 2) : 0,
            Math.Round(Pct(.5), 1),
            Math.Round(Pct(.95), 1),
            traces.Count == 0 ? 0 : Math.Round((double)errors / traces.Count, 4),
            Enum.GetValues<Outcome>().ToDictionary(o => o, o => traces.Count(t => t.Outcome == o)));
    }
}
