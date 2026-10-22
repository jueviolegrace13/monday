using Monday.Domain.Access;

namespace Monday.Application.Agent;

/// <summary>The tools a role may use on a channel. A tool that is hidden here cannot be found or run.</summary>
public sealed class ToolRegistry
{
    private readonly IReadOnlyList<IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = [.. tools];
        var duplicate = _tools.GroupBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two tools are named '{duplicate.Key}'.", nameof(tools));
        }
    }

    public IReadOnlyList<IAgentTool> For(Role role, ToolChannel channel) =>
        [.. _tools.Where(t => IsVisible(t, role, channel))];

    public IAgentTool? Find(string name, Role role, ToolChannel channel) =>
        _tools.FirstOrDefault(t => t.Name == name && IsVisible(t, role, channel));

    private static bool IsVisible(IAgentTool tool, Role role, ToolChannel channel) =>
        (tool.OnlyOn is null || tool.OnlyOn == channel)
        && !(channel == ToolChannel.Mcp && tool.Kind == ToolKind.ScreenAction)
        && tool.Requires.All(p => RolePermissions.Allows(role, p));
}
