using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;
using Monday.Agent;

namespace Monday.Domain.Tests;

/// <summary>The agent's promise: how a request moved, never what it carried.</summary>
internal static class AgentPrivacyTests
{
    private const string HashKey = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private static (MondayAgentMiddleware Middleware, TraceBuffer Buffer) Agent(RequestDelegate next)
    {
        var options = Options.Create(new MondayAgentOptions { UserHashKey = HashKey });
        var buffer = new TraceBuffer(options);
        return (new MondayAgentMiddleware(next, buffer, options), buffer);
    }

    private static DefaultHttpContext SensitiveRequest()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/search/alice@example.com";
        http.Request.QueryString = new QueryString("?email=alice@example.com&card=4111111111111111");
        http.Request.Headers.Authorization = "Bearer secret-token-123";
        http.Request.Headers.Cookie = "session=abc123";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"password\":\"hunter22\"}"));
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim(ClaimTypes.Email, "alice@example.com")], "test"));
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("search/{term}"), 0, new EndpointMetadataCollection(new AuthorizeAttribute()), "search");
        http.SetEndpoint(endpoint);
        return http;
    }

    public static void SendsTheRouteTemplateAndAHashNeverTheContent()
    {
        var (agent, buffer) = Agent(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; });
        agent.InvokeAsync(SensitiveRequest()).GetAwaiter().GetResult();

        Assert.True(buffer.Reader.TryRead(out var trace), "A trace was recorded.");
        Assert.Equal("/search/{term}", trace!.Route);
        Assert.Equal("POST", trace.Method);
        Assert.True(trace.Guards.Contains("RequireAuthorization"), "Guards come from endpoint metadata.");
        Assert.True(trace.UserHash is { Length: 32 }, "The user is a 32-hex keyed hash.");

        var json = JsonSerializer.Serialize(trace);
        foreach (var secret in new[] { "alice", "example.com", "4111", "secret-token", "Bearer", "abc123", "hunter22", "password", "203.0.113.9" })
        {
            Assert.True(!json.Contains(secret, StringComparison.OrdinalIgnoreCase), $"Trace must not contain '{secret}'.");
        }
    }

    public static void RecordsTheExceptionTypeOnlyAndRethrows()
    {
        var (agent, buffer) = Agent(_ => throw new InvalidOperationException("connection string Password=hunter22"));
        Assert.Throws<InvalidOperationException>(() => agent.InvokeAsync(SensitiveRequest()).GetAwaiter().GetResult());
        Assert.True(buffer.Reader.TryRead(out var trace), "A failed request is still recorded.");
        Assert.Equal(500, trace!.Status);
        Assert.Equal("InvalidOperationException", trace.ErrorType);
        Assert.True(!JsonSerializer.Serialize(trace).Contains("hunter22", StringComparison.Ordinal), "The exception message never leaves the app.");
    }

    public static void AnonymousRequestsCarryNoUser()
    {
        var (agent, buffer) = Agent(_ => Task.CompletedTask);
        var http = SensitiveRequest();
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        agent.InvokeAsync(http).GetAwaiter().GetResult();
        Assert.True(buffer.Reader.TryRead(out var trace) && trace!.UserHash is null, "No sign-in, no user hash.");
    }
}

internal static class AgentStatusTests
{
    public static void ReportsTheStatusTheCallerGotWhenAHandlerTurnedTheErrorIntoAResponse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MondayAgentOptions());
        var buffer = new TraceBuffer(options);
        var agent = new MondayAgentMiddleware(ctx =>
        {
            ctx.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>(new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature { Error = new KeyNotFoundException("nope") });
            ctx.Response.StatusCode = 404;
            return Task.CompletedTask;
        }, buffer, options);
        agent.InvokeAsync(new Microsoft.AspNetCore.Http.DefaultHttpContext()).GetAwaiter().GetResult();
        Assert.True(buffer.Reader.TryRead(out var t), "Recorded.");
        Assert.Equal(404, t!.Status);
        Assert.Equal("KeyNotFoundException", t.ErrorType);
    }
}
