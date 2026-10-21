using System.Text.Json;
using Monday.Domain.Agent;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Tests;

/// <summary>A conversation belongs to one person on one project; its title comes from the first message.</summary>
internal static class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly UserId Ana = new("github:ana");
    private static readonly UserId Owner = new("github:rendee");

    public static void TitleIsTheFirstMessageCutToEightyCharacters()
    {
        var brief = Conversation.Start(ProjectId.New(), Ana, "  Why is GET /items slow?  ", Now);
        Assert.Equal("Why is GET /items slow?", brief.Title);

        var cut = Conversation.Start(ProjectId.New(), Ana, new string('a', 200), Now);
        Assert.Equal(Conversation.MaxTitleLength, cut.Title.Length);
        Assert.True(cut.Title.EndsWith('…'), "A cut title says it was cut.");

        var lines = Conversation.Start(ProjectId.New(), Ana, "first line\nsecond line", Now);
        Assert.Equal("first line second line", lines.Title);
    }

    public static void StartRecordsTheFirstMessage()
    {
        var c = Conversation.Start(ProjectId.New(), Ana, "Hello", Now);

        Assert.Equal(1, c.Messages.Count);
        Assert.Equal(ConversationRole.User, c.Messages[0].Role);
        Assert.Equal("Hello", c.Messages[0].Text);
        Assert.Equal(Ana, c.Owner);
    }

    public static void MessageLongerThanFourThousandCharactersIsRefused()
    {
        Assert.Throws<DomainException>(() => Conversation.Start(ProjectId.New(), Ana, new string('x', 4001), Now), "conversation.message_too_long");
        Assert.Throws<DomainException>(() => Conversation.Start(ProjectId.New(), Ana, "   ", Now), "conversation.message_required");

        var c = Conversation.Start(ProjectId.New(), Ana, new string('x', 4000), Now);
        Assert.Throws<DomainException>(() => c.Append(ConversationMessage.User(new string('x', 4001), Now)), "conversation.message_too_long");
    }

    public static void OnlyTheOwnerCanReadIt()
    {
        var c = Conversation.Start(ProjectId.New(), Ana, "Hello", Now);

        Assert.True(c.IsVisibleTo(Ana), "The owner sees it.");
        Assert.True(!c.IsVisibleTo(Owner), "Even a project Owner does not see someone else's conversation.");
        Assert.Throws<DomainException>(() => c.EnsureVisibleTo(Owner), "conversation.not_found");
    }

    public static void ToolMessagesCarryAResultAndAssistantCallsAreKept()
    {
        var c = Conversation.Start(ProjectId.New(), Ana, "Hello", Now);
        var args = JsonDocument.Parse("""{"lens":"security"}""").RootElement.Clone();
        c.Append(ConversationMessage.Assistant(null, [new ConversationToolCall("c1", "set_lens", args)], Now));
        c.Append(ConversationMessage.Tool(new ConversationToolResult("c1", args, false), Now.AddSeconds(1)));

        Assert.Equal(3, c.Messages.Count);
        Assert.Equal("set_lens", c.Messages[1].ToolCalls[0].Name);
        Assert.Equal("c1", c.Messages[2].ToolResult!.CallId);
        Assert.Equal(Now.AddSeconds(1), c.UpdatedAt);
    }
}
