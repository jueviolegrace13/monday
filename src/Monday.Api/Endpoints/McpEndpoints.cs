using System.Threading.RateLimiting;
using Monday.Api.Security;

namespace Monday.Api.Endpoints;

/// <summary>The MCP endpoint for outside agents. Task 12 maps <c>/mcp</c> here, authenticated by personal token only.</summary>
public static class McpEndpoints
{
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
}
