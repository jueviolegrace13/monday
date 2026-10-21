using System.Text.Json;
using System.Threading.RateLimiting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monday.Api.Security;
using Monday.Application.Agent;

namespace Monday.Api.Endpoints;

/// <summary>
/// The MCP endpoint for outside agents (Claude Code, Cursor): Monday's tools at <c>/mcp</c>, Streamable HTTP, stateless,
/// signed in with a personal token only. The rules live in <see cref="McpToolService"/>; this maps them onto MCP.
/// Program and the tests wire it through <see cref="AddMondayMcp"/> and <see cref="MapMondayMcp"/>.
/// </summary>
public static class McpEndpoints
{
    public const string Route = "/mcp";

    /// <summary>One bucket per personal token: 60 calls a minute.</summary>
    public const string RateLimitPolicy = "mcp";

    public static RateLimitPartition<string> PartitionFor(HttpContext http) => RateLimitPartition.GetFixedWindowLimiter(
        PersonalTokenAuthentication.PartitionOf(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

    /// <summary>The MCP server, its tools/list and tools/call handlers, and the per-token rate limit.</summary>
    public static IServiceCollection AddMondayMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddRateLimiter(o => o.AddPolicy(RateLimitPolicy, PartitionFor));
        services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "monday", Version = "1.0.0" })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless) // no session state on a free-tier server
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
        return services;
    }

    /// <summary>
    /// Maps <c>/mcp</c>. Authorization names the personal-token scheme, so it is the only sign-in tried here: the default
    /// (dev header) scheme does not reach this endpoint. Rate limiting runs after it, so the bucket is the token's.
    /// </summary>
    public static IEndpointConventionBuilder MapMondayMcp(this IEndpointRouteBuilder app) =>
        app.MapMcp(Route)
            .RequireAuthorization(p => p.AddAuthenticationSchemes(PersonalTokenAuthentication.Scheme).RequireAuthenticatedUser())
            .RequireRateLimiting(RateLimitPolicy)
            .WithTags("MCP");

    private static async ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        var (service, token) = Resolve(request.Services);
        var specs = await service.ListAsync(token, ct);
        return new ListToolsResult
        {
            Tools = [.. specs.Select(s => new Tool { Name = s.Name, Description = s.Description, InputSchema = s.InputSchema })],
        };
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var (service, token) = Resolve(request.Services);
        var call = request.Params ?? throw new InvalidOperationException("tools/call without parameters.");
        var arguments = JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, JsonElement>());
        var outcome = await service.CallAsync(token, call.Name, arguments, ct);
        if (outcome.Error is { } error)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error }] };
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = outcome.Data!.Value.GetRawText() }],
            Meta = outcome.NodeIds.Count == 0 ? null : new System.Text.Json.Nodes.JsonObject
            {
                ["monday/nodeIds"] = new System.Text.Json.Nodes.JsonArray([.. outcome.NodeIds.Select(id => (System.Text.Json.Nodes.JsonNode?)id)]),
            },
        };
    }

    private static (McpToolService Service, Domain.Access.PersonalToken Token) Resolve(IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("MCP request outside an HTTP request.");
        return (services.GetRequiredService<McpToolService>(), http.PersonalTokenOf());
    }
}
