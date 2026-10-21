using System.Text.Json;
using Monday.Application.Agent;
using Monday.Domain.Access;

namespace Monday.Domain.Tests;

/// <summary>The agent only ever sees the tools its role and channel allow.</summary>
internal static class ToolRegistryTests
{
    private sealed class FakeTool(string name, ToolKind kind, Permission[] requires, ToolChannel? onlyOn = null) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => Name;
        public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolKind Kind { get; } = kind;
        public IReadOnlySet<Permission> Requires { get; } = requires.ToHashSet();
        public ToolChannel? OnlyOn { get; } = onlyOn;

        public Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct) =>
            Task.FromResult(new ToolResult(input, []));
    }

    private static ToolRegistry Registry(params IAgentTool[] extra) => new(
    [
        new FakeTool("read", ToolKind.Read, [Permission.ChatAsk]),
        new FakeTool("focus", ToolKind.ScreenAction, [Permission.ChatAct]),
        new FakeTool("draft", ToolKind.Proposal, [Permission.ChatAct]),
        .. extra,
    ]);

    public static void MonitorSeesOnlyAskTools() =>
        Assert.Equal(1, Registry().For(Role.Monitor, ToolChannel.Chat).Count);

    public static void DebuggerSeesActions() =>
        Assert.Equal(3, Registry().For(Role.Debugger, ToolChannel.Chat).Count);

    public static void McpDropsScreenActions() =>
        Assert.Equal(2, Registry().For(Role.Debugger, ToolChannel.Mcp).Count);

    public static void FindRefusesHiddenTool() =>
        Assert.True(Registry().Find("focus", Role.Monitor, ToolChannel.Chat) is null, "A hidden tool cannot be found.");

    public static void McpOnlyToolHiddenInChat()
    {
        var registry = Registry(new FakeTool("mcp_only", ToolKind.Read, [], ToolChannel.Mcp));
        Assert.True(registry.Find("mcp_only", Role.Monitor, ToolChannel.Mcp) is not null, "Visible on Mcp.");
        Assert.True(registry.Find("mcp_only", Role.Monitor, ToolChannel.Chat) is null, "Hidden in chat.");
    }

    public static void DuplicateNamesThrow() =>
        Assert.Throws<ArgumentException>(() => Registry(new FakeTool("read", ToolKind.Read, [])));
}
