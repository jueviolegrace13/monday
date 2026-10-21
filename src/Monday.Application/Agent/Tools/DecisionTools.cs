using System.Text.Json;
using Monday.Application.Decisions;
using Monday.Domain.Access;
using Monday.Domain.Decisions;

namespace Monday.Application.Agent.Tools;

public sealed class ListDecisionsTool(DecisionService decisions) : IAgentTool
{
    public string Name => "list_decisions";

    public string Description => "The project's decisions, newest first: area, title, options, recommendation and status. At most 50.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{"status":{"type":"string","enum":["Proposed","Approved","Rejected"]}},"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Read;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAsk };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = ToolInput.Enum<DecisionStatus>(input, "status");
        var all = await decisions.ListAsync(context.Project, context.User, ct).ConfigureAwait(false);
        var matches = all.Where(d => status is null || d.Status == status).OrderByDescending(d => d.ProposedAt).ToList();
        var page = matches.Take(ToolInput.MaxItems).ToList();
        var items = page.Select(d => new
        {
            id = d.Id.Value, d.Area, d.Title, d.Context, d.Options, d.RecommendedKey,
            supersedes = d.Supersedes?.Value, d.Status, d.ChosenKey, resolvedBy = d.ResolvedBy?.Value, d.ProposedAt, d.ResolvedAt, d.Note,
            draftedBy = d.DraftedBy?.Value,
        });
        return new ToolResult(ToolInput.Data(new { decisions = items, total = matches.Count, truncated = matches.Count > page.Count }), []);
    }
}

/// <summary>Drafts a decision for the Owner. It lands as Proposed; nothing the agent does can approve one.</summary>
public sealed class DraftDecisionTool(DecisionService decisions) : IAgentTool
{
    public string Name => "draft_decision";

    public string Description => "Draft a decision for the Owner to settle: two or three options with trade-offs and a recommendation. It waits as Proposed; only an Owner can approve it.";

    public JsonElement InputSchema { get; } = ToolInput.Schema("""
        {"type":"object","properties":{
          "area":{"type":"string","enum":["Architecture","Stack","Schema","Dependency","Security","Deploy"]},
          "title":{"type":"string","maxLength":120},
          "context":{"type":"string","maxLength":2000,"description":"What is being decided and why now."},
          "options":{"type":"array","minItems":2,"maxItems":3,"items":{"type":"object","properties":{
            "key":{"type":"string"},"title":{"type":"string"},"tradeOffs":{"type":"string"}},
            "required":["key","title","tradeOffs"],"additionalProperties":false}},
          "recommendedKey":{"type":"string","description":"The key of the option you recommend."},
          "supersedes":{"type":"string","description":"Id of an earlier decision this replaces."}},
         "required":["area","title","context","options","recommendedKey"],"additionalProperties":false}
        """);

    public ToolKind Kind => ToolKind.Proposal;

    public IReadOnlySet<Permission> Requires { get; } = new HashSet<Permission> { Permission.ChatAct };

    public ToolChannel? OnlyOn => null;

    public async Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var area = Enum.TryParse<DecisionArea>(ToolInput.RequiredString(input, "area"), true, out var a) ? a : throw ToolInput.Invalid("'area' is not a decision area.");
        if (!input.TryGetProperty("options", out var raw) || raw.ValueKind != JsonValueKind.Array)
        {
            throw ToolInput.Invalid("'options' must be a list.");
        }

        var options = raw.EnumerateArray()
            .Select(o => new DecisionOption(ToolInput.RequiredString(o, "key"), ToolInput.RequiredString(o, "title"), ToolInput.RequiredString(o, "tradeOffs")))
            .ToList();
        Guid? supersedes = ToolInput.String(input, "supersedes") is { } s ? Guid.TryParse(s, out var g) ? g : throw ToolInput.Invalid("'supersedes' must be a decision id.") : null;

        var proposal = new DecisionProposal(area, ToolInput.RequiredString(input, "title"), ToolInput.RequiredString(input, "context"), options, ToolInput.RequiredString(input, "recommendedKey"), supersedes);
        var decision = await decisions.DraftAsync(context.Project, proposal, context.User, ct).ConfigureAwait(false);
        return new ToolResult(ToolInput.Data(new { id = decision.Id.Value, decision.Title, decision.Status, decision.RecommendedKey }), []);
    }
}
