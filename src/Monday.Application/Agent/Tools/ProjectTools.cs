using System.Text.Json;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Projects;

namespace Monday.Application.Agent.Tools;

/// <summary>MCP only: a client has no open project, so it asks which ones the caller belongs to.</summary>
public sealed class ListProjectsTool(ProjectService projects) : IAgentTool
{
    public string Name => "list_projects";

    public string Description => "The projects you belong to, with your role and their environments. Use a project's id as 'project' in the other tools.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""{"type":"object","properties":{},"additionalProperties":false}""");

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission>();

    public ToolChannel? OnlyOn => ToolChannel.Mcp;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var all = await projects.ListAsync(context.User, ct).ConfigureAwait(false);
        IReadOnlyList<ProjectSummary> mine = context.Reach is { } reach ? [.. all.Where(p => reach.Contains(new ProjectId(p.Id)))] : all;
        var page = mine.Take(ToolInput.MaxItems).ToList();
        return new ToolResult(ToolInput.Data(new { projects = page, total = mine.Count, truncated = mine.Count > page.Count }), []);
    }
}
