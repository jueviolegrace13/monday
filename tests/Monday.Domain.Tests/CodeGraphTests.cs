using Monday.Domain.Activity;
using Monday.Domain.CodeGraph;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Tests;

internal static class CodeGraphTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 7, 0, 0, TimeSpan.Zero);
    private static readonly ProjectId Project = ProjectId.New();

    private static CodeGraphSnapshot Sample() => CodeGraphSnapshot.Create(Project, "abc123", Now,
        [
            new CodeNode { Id = "app", Kind = NodeKind.App, Name = "Monday" },
            new CodeNode { Id = "GET /projects", Kind = NodeKind.Endpoint, Name = "GET /projects", ParentId = "app", Route = "GET /projects" },
            new CodeNode { Id = "List", Kind = NodeKind.Method, Name = "List", ParentId = "app", Code = "a", CodeHash = CodeNode.HashCode("a") },
            new CodeNode { Id = "Load", Kind = NodeKind.Method, Name = "Load", ParentId = "app" },
            new CodeNode { Id = "LoadTest", Kind = NodeKind.Test, Name = "LoadTest", ParentId = "app" },
        ],
        [
            new CodeEdge("GET /projects", "List", EdgeKind.HandledBy),
            new CodeEdge("List", "Load", EdgeKind.Calls),
            new CodeEdge("LoadTest", "Load", EdgeKind.Covers),
        ]);

    public static void AnswersCallersTestsAndEndpoints()
    {
        var graph = Sample();

        Assert.Equal("List", graph.Callers("Load").Single().Id);
        Assert.Equal("LoadTest", graph.CoveringTests("Load").Single().Id);
        Assert.Equal("GET /projects", graph.EndpointsReaching("Load").Single().Id);
    }

    public static void RejectsDanglingEdgesAndDuplicates()
    {
        var app = new CodeNode { Id = "app", Kind = NodeKind.App, Name = "Monday" };
        Assert.Throws<DomainException>(() => CodeGraphSnapshot.Create(Project, "c", Now, [app], [new CodeEdge("app", "ghost", EdgeKind.Calls)]), "graph.dangling_edge");
        Assert.Throws<DomainException>(() => CodeGraphSnapshot.Create(Project, "c", Now, [app, app], []), "graph.duplicate_node");
    }

    public static void ExplanationGoesStaleWhenCodeChanges()
    {
        var node = Sample().Find("List")!;
        var explanation = new Explanation("List", node.CodeHash!, "Lists projects.", Now);

        Assert.True(explanation.IsCurrentFor(node), "Fresh explanation is current.");
        Assert.True(!explanation.IsCurrentFor(node with { CodeHash = CodeNode.HashCode("b") }), "Changed code makes it stale.");
    }

    public static void ActivityEventIsBoundedAndClean()
    {
        var e = ActivityEvent.Record(Project, "s1", Now, ActivityKind.Edit, " Edited List ", ["List", "List", " "]);

        Assert.Equal("Edited List", e.Summary);
        Assert.Equal(1, e.NodeIds.Count);
        Assert.Throws<DomainException>(() => ActivityEvent.Record(Project, "s1", Now, ActivityKind.Edit, new string('x', 300)), "activity.summary_required");
    }
}
