using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.CodeGraph;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.CodeGraph;

public sealed record GraphUpload(string Commit, IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges);

public sealed record ExplanationUpload(string NodeId, string CodeHash, string Text);

public sealed record GraphView(string Commit, DateTimeOffset IndexedAt, IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges);

public sealed record NodeDetail(
    CodeNode Node,
    string? Explanation,
    bool ExplanationIsCurrent,
    IReadOnlyList<CodeNode> Callers,
    IReadOnlyList<CodeNode> Callees,
    IReadOnlyList<CodeNode> Tests,
    IReadOnlyList<CodeNode> Endpoints);

/// <summary>Receives code graphs from CI and serves them to the viewer, filtered by what the person may see.</summary>
public sealed class GraphService(IGraphStore graphs, ProjectService projects, IClock clock)
{
    /// <summary>CI uploads the graph for the commit it just built. Read-only toward the app: production keys may upload too.</summary>
    public async Task<int> IngestAsync(AppReporter reporter, GraphUpload upload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(upload);
        var snapshot = CodeGraphSnapshot.Create(reporter.Project.Id, upload.Commit, clock.Now, upload.Nodes, upload.Edges);
        await graphs.SaveAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot.Nodes.Count;
    }

    public async Task<int> IngestExplanationsAsync(AppReporter reporter, IReadOnlyList<ExplanationUpload> uploads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(uploads);
        var explanations = uploads
            .Select(u => new Explanation(
                Guard.NotBlank(u.NodeId, "explanation.node_required", "An explanation needs a node id", 1024),
                Guard.NotBlank(u.CodeHash, "explanation.hash_required", "An explanation needs the code hash it explains", 64),
                Guard.NotBlank(u.Text, "explanation.text_required", "An explanation needs text", 4000),
                clock.Now))
            .ToList();
        await graphs.SaveExplanationsAsync(reporter.Project.Id, explanations, ct).ConfigureAwait(false);
        return explanations.Count;
    }

    /// <summary>The whole graph. People without <see cref="Permission.ViewCode"/> get the shape only, never source.</summary>
    public async Task<GraphView?> GetAsync(ProjectId id, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ViewLiveGraph);
        var snapshot = await graphs.LatestAsync(id, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var nodes = project.Can(user, Permission.ViewCode)
            ? snapshot.Nodes
            : snapshot.Nodes.Select(WithoutSource).ToList();
        return new GraphView(snapshot.Commit, snapshot.IndexedAt, nodes, snapshot.Edges);
    }

    public async Task<NodeDetail> GetNodeAsync(ProjectId id, string nodeId, UserId user, CancellationToken ct)
    {
        var project = await projects.LoadForMemberAsync(id, user, ct).ConfigureAwait(false);
        project.EnsureCan(user, Permission.ViewCode);
        var snapshot = await graphs.LatestAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("graph");
        var node = snapshot.Find(nodeId) ?? throw new NotFoundException("node");
        var explanations = await graphs.ExplanationsAsync(id, ct).ConfigureAwait(false);
        explanations.TryGetValue(nodeId, out var explanation);

        return new NodeDetail(
            node,
            explanation?.Text,
            explanation?.IsCurrentFor(node) ?? false,
            snapshot.Callers(nodeId).ToList(),
            snapshot.Callees(nodeId).ToList(),
            snapshot.CoveringTests(nodeId).ToList(),
            snapshot.EndpointsReaching(nodeId).ToList());
    }

    private static CodeNode WithoutSource(CodeNode node) => node with { Code = null };
}
