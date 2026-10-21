using System.Text.Json;
using System.Text.Json.Nodes;
using Monday.Application.Abstractions;
using Monday.Application.Access;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Agent;

/// <summary>What a call over MCP returns: the tool's data and the nodes it touched, or a plain error for the agent.</summary>
public sealed record McpToolOutcome(JsonElement? Data, IReadOnlyList<string> NodeIds, string? Error)
{
    public bool IsError => Error is not null;

    public static McpToolOutcome Failed(string message) => new(null, [], message);
}

/// <summary>
/// Monday's tools for an outside agent signed in with a personal token. The list is the union, over the projects the
/// token may reach, of what the member's role sees on the MCP channel, cut down to what the token's scope allows.
/// Every call checks again, at call time: real membership and role on the named project, the token's limits, and the
/// registry, so a token for a project its holder has since left stops working there.
/// </summary>
public sealed class McpToolService(IProjectRepository projects, ToolRegistry tools)
{
    /// <summary>The argument every tool but <c>list_projects</c> takes over MCP: which project to run on.</summary>
    public const string ProjectArgument = "project";

    /// <summary>The one tool that needs no project: it says which projects there are.</summary>
    public const string ListProjects = "list_projects";

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<IReadOnlyList<ToolSpec>> ListAsync(PersonalToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        var reach = await ReachAsync(token, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var specs = new List<ToolSpec>();
        foreach (var (project, role) in reach)
        {
            foreach (var tool in tools.For(role, ToolChannel.Mcp).Where(t => token.Allows(project, t.Kind) && seen.Add(t.Name)))
            {
                specs.Add(tool.Name == ListProjects ? tool.ToSpec() : new ToolSpec(tool.Name, tool.Description, WithProject(tool.InputSchema)));
            }
        }

        return specs;
    }

    /// <summary>Runs one call as the token's holder. Never throws for a bad call: the agent gets a short error instead.</summary>
    public async Task<McpToolOutcome> CallAsync(PersonalToken token, string name, JsonElement arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        var args = arguments.ValueKind == JsonValueKind.Object ? arguments : EmptyObject;
        var reach = await ReachAsync(token, ct).ConfigureAwait(false);
        var reachable = reach.Select(r => r.Project).ToHashSet();

        ProjectId project;
        Role role;
        if (name == ListProjects)
        {
            // It reads only the caller's own memberships (within the token's reach); any reachable project carries it.
            if (reach.Count == 0)
            {
                return McpToolOutcome.Failed("agent.unknown_tool: No such tool is available.");
            }

            (project, role) = reach[0];
        }
        else
        {
            if (!(args.TryGetProperty(ProjectArgument, out var raw) && raw.ValueKind == JsonValueKind.String && Guid.TryParse(raw.GetString(), out var id)))
            {
                return McpToolOutcome.Failed("agent.input: 'project' is required: a project id from list_projects.");
            }

            project = new ProjectId(id);
            var asked = project;
            if (reach.Where(r => r.Project == asked).Select(r => (Role?)r.Role).FirstOrDefault() is not { } current)
            {
                // Not a member, left the project, or outside the token's list: all look the same.
                return McpToolOutcome.Failed("not found: project");
            }

            role = current;
        }

        var tool = tools.Find(name, role, ToolChannel.Mcp);
        if (tool is null || !token.Allows(project, tool.Kind))
        {
            return McpToolOutcome.Failed("agent.unknown_tool: No such tool is available.");
        }

        var context = new ToolContext(project, token.UserId, role, ToolChannel.Mcp, reachable);
        try
        {
            var result = await tool.RunAsync(context, WithoutProject(args), ct).ConfigureAwait(false);
            return new McpToolOutcome(result.Data, result.NodeIds, null);
        }
        catch (AccessDeniedException)
        {
            return McpToolOutcome.Failed("not allowed");
        }
        catch (NotFoundException e)
        {
            return McpToolOutcome.Failed($"not found: {e.What}");
        }
        catch (DomainException e)
        {
            return McpToolOutcome.Failed($"{e.Code}: {e.Message}");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A bug in a tool must not show the agent a stack trace.
            return McpToolOutcome.Failed("agent.tool_failed: The tool failed.");
        }
    }

    /// <summary>The projects the token reaches, each with the holder's role there now: real membership, then the token's list.</summary>
    private async Task<IReadOnlyList<(ProjectId Project, Role Role)>> ReachAsync(PersonalToken token, CancellationToken ct)
    {
        var mine = await projects.ListForMemberAsync(token.UserId, ct).ConfigureAwait(false);
        return [.. mine
            .Where(p => token.AllowsProject(p.Id))
            .Select(p => (p.Id, Role: p.RoleOf(token.UserId)))
            .Where(r => r.Role is not null)
            .Select(r => (r.Id, r.Role!.Value))];
    }

    /// <summary>The tool's schema plus a required <c>project</c> string.</summary>
    private static JsonElement WithProject(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var properties = node["properties"] as JsonObject ?? [];
        properties[ProjectArgument] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "The project's id, from list_projects.",
        };
        node["properties"] = properties;

        var required = node["required"] as JsonArray ?? [];
        required.Add(ProjectArgument);
        node["required"] = required;
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement WithoutProject(JsonElement args)
    {
        var node = new JsonObject();
        foreach (var property in args.EnumerateObject().Where(p => p.Name != ProjectArgument))
        {
            node[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return JsonSerializer.SerializeToElement(node);
    }
}
