using System.Text.Json;
using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Agent;

/// <summary>What a tool does: reads, changes the screen, or proposes a decision.</summary>
public enum ToolKind
{
    Read,
    ScreenAction,
    Proposal,
}

/// <summary>Where the agent is being used: the chat inside the graph, or the MCP endpoint.</summary>
public enum ToolChannel
{
    Chat,
    Mcp,
}

/// <summary>
/// Who is asking, on which project, in which role and channel. <paramref name="Reach"/> is the projects the caller's
/// credential may see (a personal token limited to some projects); null means all of the caller's projects.
/// </summary>
public sealed record ToolContext(ProjectId Project, UserId User, Role Role, ToolChannel Channel, IReadOnlySet<ProjectId>? Reach = null);

/// <summary>A change to the screen. Type is one of focus_node, highlight_path, set_lens, start_replay.</summary>
public sealed record ScreenAction(string Type, JsonElement Args);

/// <summary>What a tool returns: data for the model, the graph nodes it touched, and an optional screen action.</summary>
public sealed record ToolResult(JsonElement Data, IReadOnlyList<string> NodeIds, ScreenAction? Action = null);

/// <summary>The part of a tool the model is shown.</summary>
public sealed record ToolSpec(string Name, string Description, JsonElement InputSchema);

/// <summary>One thing the agent can do, independent of which model provider drives it.</summary>
public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    JsonElement InputSchema { get; }

    ToolKind Kind { get; }

    IReadOnlySet<Permission> Requires { get; }

    /// <summary>The only channel this tool is offered on; null means both.</summary>
    ToolChannel? OnlyOn { get; }

    Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct);
}

public static class AgentToolExtensions
{
    public static ToolSpec ToSpec(this IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new ToolSpec(tool.Name, tool.Description, tool.InputSchema);
    }
}
