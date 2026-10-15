using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monday.Indexer;

// monday-index: reads a repo's C# code and writes Monday's code graph.
//   monday-index --root <repo> [--name <app name>] [--out .monday/graph.json] [--commit <sha>] [--upload <monday url>]
// --upload sends the graph to Monday with the key in the MONDAY_KEY environment variable (never a flag, so it stays out of logs).

var options = IndexerOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: monday-index --root <repo> [--name <app>] [--out <file>] [--commit <sha>] [--upload <url>]");
    return 2;
}

var graph = new RepoIndexer(options.Root, options.Name).Index(options.Commit ?? Git.HeadCommit(options.Root));
var json = JsonSerializer.Serialize(graph, IndexerOptions.Json);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
await File.WriteAllTextAsync(options.Out, json);
Console.WriteLine($"Indexed {graph.Nodes.Count} nodes and {graph.Edges.Count} edges at {graph.Commit} -> {options.Out}");

if (options.Upload is not null)
{
    var key = Environment.GetEnvironmentVariable("MONDAY_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("--upload needs the MONDAY_KEY environment variable.");
        return 3;
    }

    using var http = new HttpClient { BaseAddress = new Uri(options.Upload) };
    http.DefaultRequestHeaders.Add("X-Monday-Key", key);
    using var response = await http.PostAsJsonAsync("ingest/graph", graph, IndexerOptions.Json);
    Console.WriteLine($"Upload: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    return response.IsSuccessStatusCode ? 0 : 4;
}

return 0;

internal sealed record IndexerOptions(string Root, string Name, string Out, string? Commit, string? Upload)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IndexerOptions? Parse(string[] args)
    {
        string? Value(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var root = Value("--root");
        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        root = Path.GetFullPath(root);
        return new IndexerOptions(
            root,
            Value("--name") ?? new DirectoryInfo(root).Name,
            Value("--out") ?? Path.Combine(root, ".monday", "graph.json"),
            Value("--commit"),
            Value("--upload"));
    }
}
