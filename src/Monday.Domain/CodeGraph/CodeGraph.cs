using System.Security.Cryptography;
using System.Text;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.CodeGraph;

/// <summary>What a node in the graph stands for, from the whole app down to one method.</summary>
public enum NodeKind
{
    App,
    Module,
    Feature,
    Type,
    Method,
    Endpoint,
    Test,
}

/// <summary>How two nodes relate.</summary>
public enum EdgeKind
{
    /// <summary>A module contains a type, a type contains a method.</summary>
    Contains,

    /// <summary>One method calls another.</summary>
    Calls,

    /// <summary>A type depends on another (constructor parameter, field, base type).</summary>
    DependsOn,

    /// <summary>A service is registered for an interface in dependency injection.</summary>
    Implements,

    /// <summary>A test exercises a method.</summary>
    Covers,

    /// <summary>An endpoint is handled by a method.</summary>
    HandledBy,
}

/// <summary>
/// One part of the code. <see cref="Id"/> is stable across commits (a fully qualified name),
/// so explanations, comments and activity stay attached as the code changes.
/// </summary>
public sealed record CodeNode
{
    public required string Id { get; init; }

    public required NodeKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>Parent in the containment tree (null for the app node).</summary>
    public string? ParentId { get; init; }

    /// <summary>Repo-relative file path, when the node lives in one file.</summary>
    public string? File { get; init; }

    public int? StartLine { get; init; }

    public int? EndLine { get; init; }

    /// <summary>Source text of the node (methods and endpoints only).</summary>
    public string? Code { get; init; }

    /// <summary>Hash of <see cref="Code"/>; when it changes, the explanation is out of date.</summary>
    public string? CodeHash { get; init; }

    /// <summary>For endpoints: HTTP method and route template, e.g. "GET /projects/{id}".</summary>
    public string? Route { get; init; }

    /// <summary>Security guards seen on the node (for example "RequireAuthorization", "RateLimit:ingest").</summary>
    public IReadOnlyList<string> Guards { get; init; } = [];

    public static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.ReplaceLineEndings("\n"))))[..16];
}

public sealed record CodeEdge(string From, string To, EdgeKind Kind);

/// <summary>
/// The whole code graph of one project at one commit, as produced by the indexer in CI.
/// A snapshot is immutable: a new commit means a new snapshot, so replay can show the code as it was.
/// </summary>
public sealed class CodeGraphSnapshot
{
    private CodeGraphSnapshot(ProjectId projectId, string commit, DateTimeOffset indexedAt, IReadOnlyList<CodeNode> nodes, IReadOnlyList<CodeEdge> edges)
    {
        ProjectId = projectId;
        Commit = commit;
        IndexedAt = indexedAt;
        Nodes = nodes;
        Edges = edges;
    }

    public ProjectId ProjectId { get; }

    public string Commit { get; }

    public DateTimeOffset IndexedAt { get; }

    public IReadOnlyList<CodeNode> Nodes { get; }

    public IReadOnlyList<CodeEdge> Edges { get; }

    /// <summary>Validates and builds a snapshot: ids unique, every edge and parent points at a node in the graph.</summary>
    public static CodeGraphSnapshot Create(ProjectId projectId, string commit, DateTimeOffset indexedAt, IReadOnlyList<CodeNode> nodes, IReadOnlyList<CodeEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        commit = Guard.NotBlank(commit, "graph.commit_required", "A graph snapshot needs the commit it was indexed from", 64);
        Guard.That(nodes.Count > 0, "graph.empty", "A graph snapshot needs at least one node.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            Guard.NotBlank(node.Id, "graph.node_id_required", "Every node needs an id", 1024);
            Guard.That(ids.Add(node.Id), "graph.duplicate_node", $"Node '{node.Id}' appears twice.");
        }

        foreach (var node in nodes.Where(n => n.ParentId is not null))
        {
            Guard.That(ids.Contains(node.ParentId!), "graph.unknown_parent", $"Node '{node.Id}' has an unknown parent '{node.ParentId}'.");
        }

        foreach (var edge in edges)
        {
            Guard.That(ids.Contains(edge.From) && ids.Contains(edge.To), "graph.dangling_edge", $"Edge {edge.From} -> {edge.To} points outside the graph.");
        }

        return new CodeGraphSnapshot(projectId, commit, indexedAt, nodes, edges.Distinct().ToList());
    }

    public CodeNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>Who calls this node.</summary>
    public IEnumerable<CodeNode> Callers(string id) => Related(e => e.Kind == EdgeKind.Calls && e.To == id, e => e.From);

    /// <summary>What this node calls.</summary>
    public IEnumerable<CodeNode> Callees(string id) => Related(e => e.Kind == EdgeKind.Calls && e.From == id, e => e.To);

    /// <summary>Tests that exercise this node.</summary>
    public IEnumerable<CodeNode> CoveringTests(string id) => Related(e => e.Kind == EdgeKind.Covers && e.To == id, e => e.From);

    /// <summary>Endpoints whose handling reaches this node through any chain of calls.</summary>
    public IEnumerable<CodeNode> EndpointsReaching(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var queue = new Queue<string>([id]);
        var endpoints = new List<CodeNode>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in Edges.Where(e => e.To == current && e.Kind is EdgeKind.Calls or EdgeKind.HandledBy))
            {
                if (!seen.Add(edge.From))
                {
                    continue;
                }

                if (Find(edge.From) is { Kind: NodeKind.Endpoint } endpoint)
                {
                    endpoints.Add(endpoint);
                }

                queue.Enqueue(edge.From);
            }
        }

        return endpoints;
    }

    private IEnumerable<CodeNode> Related(Func<CodeEdge, bool> match, Func<CodeEdge, string> pick) =>
        Edges.Where(match).Select(pick).Distinct().Select(Find).OfType<CodeNode>();
}

/// <summary>Claude's plain-English explanation of a node, tied to the code it explained.</summary>
public sealed record Explanation(string NodeId, string CodeHash, string Text, DateTimeOffset WrittenAt)
{
    public bool IsCurrentFor(CodeNode node) => node.CodeHash is not null && node.CodeHash == CodeHash;
}
