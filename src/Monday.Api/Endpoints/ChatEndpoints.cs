using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Monday.Api.Security;
using Monday.Application.Agent;
using Monday.Domain.Projects;

namespace Monday.Api.Endpoints;

/// <summary>The agent chat and each person's own history. Closing the stream (Stop) cancels the loop.</summary>
public static class ChatEndpoints
{
    /// <summary>One token bucket per signed-in person: 20 questions a minute.</summary>
    public const string RateLimitPolicy = "chat";

    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        var chat = app.MapGroup("/projects/{id:guid}").WithTags("Chat").RequireAuthorization();
        chat.MapPost("/chat", Chat).RequireRateLimiting(RateLimitPolicy);
        chat.MapGet("/conversations", ListConversations);
        chat.MapGet("/conversations/{cid:guid}", GetConversation);
        return app;
    }

    /// <summary>
    /// Server-sent events: one event per <see cref="AgentEvent"/> (started, text, tool, action, done, error). Errors of the
    /// request itself (not a member, no ChatAsk, a message too long, someone else's conversation) are found before
    /// the first event, so they still come back as a plain problem response with its status.
    /// </summary>
    private static async Task Chat(Guid id, ChatInput input, HttpContext http, ClaimsPrincipal user, AgentService agent, IOptions<JsonOptions> json)
    {
        var ct = http.RequestAborted;
        var options = json.Value.SerializerOptions;
        await using var events = agent.ChatAsync(new ProjectId(id), user.UserIdOf(), input, ct).GetAsyncEnumerator(ct);
        try
        {
            var more = await events.MoveNextAsync();
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            while (more)
            {
                var (name, data) = Frame(events.Current);
                await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, data.GetType(), options)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                more = await events.MoveNextAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The viewer pressed Stop or left; the loop has stopped and kept what finished.
        }
    }

    private static (string Name, object Data) Frame(AgentEvent e) => e switch
    {
        AgentStarted s => ("started", s),
        AgentText t => ("text", t),
        AgentTool t => ("tool", t),
        AgentAction a => ("action", a.Action),
        AgentDone d => ("done", d),
        AgentError x => ("error", x),
        _ => throw new InvalidOperationException($"Unknown agent event {e.GetType().Name}."),
    };

    private static async Task<IResult> ListConversations(Guid id, ClaimsPrincipal user, AgentService agent, CancellationToken ct) =>
        Results.Ok(await agent.ListConversationsAsync(new ProjectId(id), user.UserIdOf(), ct));

    private static async Task<IResult> GetConversation(Guid id, Guid cid, ClaimsPrincipal user, AgentService agent, CancellationToken ct) =>
        Results.Ok(await agent.GetConversationAsync(new ProjectId(id), cid, user.UserIdOf(), ct));
}
