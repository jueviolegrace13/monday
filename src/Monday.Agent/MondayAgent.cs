using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Monday.Agent;

/// <summary>Settings, usually from the "Monday:Agent" configuration section and environment variables.</summary>
public sealed class MondayAgentOptions
{
    /// <summary>Monday's base URL, e.g. https://monday.onrender.com/.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The project's API key for this environment. From the environment, never from a file in the repo.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The project's user-hash key (hex), so user ids are hashed before they leave the app.</summary>
    public string? UserHashKey { get; set; }

    /// <summary>Route templates (prefixes) that are not reported, e.g. health checks.</summary>
    public IList<string> ExcludeRoutes { get; } = [];

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 200;

    /// <summary>Requests held while Monday is unreachable (e.g. waking from sleep). Oldest are dropped past this.</summary>
    public int BufferLimit { get; set; } = 10_000;

    public bool IsConfigured => Endpoint is not null && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>One request, in the shape Monday's /ingest/traffic takes. There is nowhere to put a body, query, header, IP or name.</summary>
public sealed record AgentTrace(string TraceId, DateTimeOffset At, string Method, string Route, int Status, double DurationMs, IReadOnlyList<string> Guards, string? UserHash, string? ErrorType);

/// <summary>Where finished requests go. The default sends them to Monday over HTTPS; Monday itself can plug in its own.</summary>
public interface ITraceSink
{
    Task SendAsync(IReadOnlyList<AgentTrace> batch, CancellationToken ct);
}

public static class MondayAgentExtensions
{
    /// <summary>Registers the agent. Does nothing when Monday's endpoint or key isn't configured, so local runs stay quiet.</summary>
    public static IServiceCollection AddMondayAgent(this IServiceCollection services, IConfiguration configuration, Action<MondayAgentOptions>? configure = null)
    {
        services.Configure<MondayAgentOptions>(configuration.GetSection("Monday:Agent"));
        if (configure is not null)
        {
            services.PostConfigure(configure);
        }

        services.AddSingleton<TraceBuffer>();
        services.AddHttpClient(HttpTraceSink.ClientName);
        services.AddSingleton<HttpTraceSink>();
        services.AddHostedService<TraceSender>();
        return services;
    }

    /// <summary>
    /// Put first in the pipeline, before UseExceptionHandler, so every request is timed (including ones
    /// a guard rejects) and the status recorded is the one the caller actually received.
    /// </summary>
    public static IApplicationBuilder UseMondayAgent(this IApplicationBuilder app) => app.UseMiddleware<MondayAgentMiddleware>();
}

/// <summary>Times each request and records how it ended. Reads nothing from the request but its method and matched route.</summary>
public sealed class MondayAgentMiddleware(RequestDelegate next, TraceBuffer buffer, IOptions<MondayAgentOptions> options)
{
    private readonly byte[]? _userKey = string.IsNullOrWhiteSpace(options.Value.UserHashKey) ? null : Convert.FromHexString(options.Value.UserHashKey);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = Stopwatch.GetTimestamp();
        var at = DateTimeOffset.UtcNow;
        // Routing has already matched by now (WebApplication routes first); keep it, because an
        // exception handler clears the endpoint when it re-runs the pipeline for its error response.
        var matched = context.GetEndpoint();
        string? errorType = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            // When an exception handler inside the pipeline turned the error into a response (a 404 problem,
            // say), report the status the caller really got and the type of the error it handled.
            errorType ??= context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error.GetType().Name;
            Record(context, matched ?? context.GetEndpoint(), at, Stopwatch.GetElapsedTime(started), errorType, rethrown: errorType is not null && context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>() is null);
        }
    }

    private void Record(HttpContext context, Endpoint? endpoint, DateTimeOffset at, TimeSpan elapsed, string? errorType, bool rethrown)
    {
        var route = RouteOf(endpoint);
        if (options.Value.ExcludeRoutes.Any(prefix => route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        buffer.Add(new AgentTrace(
            TraceIdOf(context),
            at,
            context.Request.Method,
            route,
            rethrown ? StatusCodes.Status500InternalServerError : context.Response.StatusCode,
            Math.Round(elapsed.TotalMilliseconds, 2),
            GuardsOf(endpoint),
            HashUser(context.User),
            errorType));
    }

    /// <summary>The route template, never the concrete path: "/projects/{id:guid}", not "/projects/7f3…".</summary>
    public static string RouteOf(Endpoint? endpoint)
    {
        var raw = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrEmpty(raw))
        {
            return "/{unmatched}";
        }

        var template = "/" + raw.TrimStart('/');
        return template.Length > 1 ? template.TrimEnd('/') : template;
    }

    /// <summary>Guards read from the endpoint's metadata, the same names Monday's code graph uses.</summary>
    public static IReadOnlyList<string> GuardsOf(Endpoint? endpoint)
    {
        if (endpoint is null)
        {
            return [];
        }

        var guards = new List<string>();
        var metadata = endpoint.Metadata;
        if (metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            guards.Add("AllowAnonymous");
        }
        else if (metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0)
        {
            var policies = metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => !string.IsNullOrEmpty(p)).ToList();
            guards.Add(policies.Count > 0 ? "RequireAuthorization:" + string.Join(',', policies) : "RequireAuthorization");
        }

        if (metadata.GetMetadata<EnableRateLimitingAttribute>() is { } limit)
        {
            guards.Add("RequireRateLimiting:" + limit.PolicyName);
        }

        if (metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
        {
            guards.Add("DisableRateLimiting");
        }

        if (metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: false })
        {
            guards.Add("DisableAntiforgery");
        }

        return guards;
    }

    private string? HashUser(ClaimsPrincipal user)
    {
        if (_userKey is null || user.Identity?.IsAuthenticated != true || user.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } id)
        {
            return null;
        }

        return Convert.ToHexString(HMACSHA256.HashData(_userKey, Encoding.UTF8.GetBytes(id.Trim())))[..32].ToLowerInvariant();
    }

    private static string TraceIdOf(HttpContext context)
    {
        var id = Activity.Current?.TraceId.ToHexString();
        return id is { Length: 32 } && id != "00000000000000000000000000000000" ? id : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }
}

/// <summary>Holds finished requests until the sender ships them. Never blocks a request: when full, the oldest go.</summary>
public sealed class TraceBuffer
{
    private readonly Channel<AgentTrace> _channel;
    private long _dropped;

    public TraceBuffer(IOptions<MondayAgentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _channel = Channel.CreateBounded<AgentTrace>(new BoundedChannelOptions(Math.Max(100, options.Value.BufferLimit))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }, _ => Interlocked.Increment(ref _dropped));
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Add(AgentTrace trace) => _channel.Writer.TryWrite(trace);

    public ChannelReader<AgentTrace> Reader => _channel.Reader;
}

/// <summary>Sends batches to Monday's /ingest/traffic with the project key.</summary>
public sealed class HttpTraceSink(IHttpClientFactory clients, IOptions<MondayAgentOptions> options) : ITraceSink
{
    public const string ClientName = "monday-agent";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task SendAsync(IReadOnlyList<AgentTrace> batch, CancellationToken ct)
    {
        var o = options.Value;
        using var http = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(o.Endpoint!, "ingest/traffic")) { Content = JsonContent.Create(batch, options: Json) };
        request.Headers.Add("X-Monday-Key", o.ApiKey);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Ships batches every couple of seconds. If Monday is unreachable (asleep on a free tier, a deploy, a
/// network blip) it keeps the batch and retries with backoff, so requests arrive late rather than never.
/// </summary>
public sealed partial class TraceSender(TraceBuffer buffer, IServiceProvider services, IOptions<MondayAgentOptions> options, ILogger<TraceSender> log) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Monday agent is not configured (Monday:Agent:Endpoint and ApiKey); requests are not reported.")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Monday unreachable ({Error}); keeping {Count} requests and retrying in {Delay}s.")]
    private partial void LogUnreachable(string error, int count, double delay);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sink = services.GetService<ITraceSink>() ?? (options.Value.IsConfigured ? services.GetRequiredService<HttpTraceSink>() : null);
        if (sink is null)
        {
            LogNotConfigured();
            return;
        }

        var batch = new List<AgentTrace>(options.Value.BatchSize);
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                window.CancelAfter(options.Value.FlushInterval);
                while (batch.Count < options.Value.BatchSize && await buffer.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                {
                    while (batch.Count < options.Value.BatchSize && buffer.Reader.TryRead(out var trace))
                    {
                        batch.Add(trace);
                    }
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Flush interval reached.
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                await sink.SendAsync(batch, stoppingToken).ConfigureAwait(false);
                batch.Clear();
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                LogUnreachable(ex.GetType().Name, batch.Count, backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }
}
