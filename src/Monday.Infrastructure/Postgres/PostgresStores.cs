using System.Text.Json;
using System.Text.Json.Serialization;
using Monday.Application.Abstractions;
using Monday.Application.Traffic;
using Monday.Domain.Access;
using Monday.Domain.Activity;
using Monday.Domain.Audit;
using Monday.Domain.CodeGraph;
using Monday.Domain.Common;
using Monday.Domain.Decisions;
using Monday.Domain.Projects;
using Monday.Domain.Traffic;
using Npgsql;
using NpgsqlTypes;

namespace Monday.Infrastructure.Postgres;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}

/// <summary>Projects as rows: the aggregate's small collections as JSON, keys and members in their own tables for lookups.</summary>
public sealed class PostgresProjectRepository(NpgsqlDataSource db) : IProjectRepository
{
    private sealed record EnvRow(Guid Id, string Name, EnvironmentKind Kind);

    private sealed record MemberRow(string UserId, Role Role);

    public Task<Project?> GetAsync(ProjectId id, CancellationToken ct) =>
        LoadAsync("SELECT id, name, created_at, user_hash_key, environments, members FROM projects WHERE id = @id", c => c.Parameters.AddWithValue("id", id.Value), ct);

    public Task<Project?> FindByKeyPrefixAsync(string prefix, CancellationToken ct) =>
        LoadAsync("SELECT p.id, p.name, p.created_at, p.user_hash_key, p.environments, p.members FROM projects p JOIN api_keys k ON k.project_id = p.id WHERE k.prefix = @prefix",
            c => c.Parameters.AddWithValue("prefix", prefix), ct);

    public async Task<IReadOnlyList<Project>> ListForMemberAsync(UserId user, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using (var cmd = db.CreateCommand("SELECT project_id FROM project_members WHERE user_id = @u"))
        {
            cmd.Parameters.AddWithValue("u", user.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        var list = new List<Project>();
        foreach (var id in ids)
        {
            if (await GetAsync(new ProjectId(id), ct).ConfigureAwait(false) is { } p)
            {
                list.Add(p);
            }
        }

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SaveAsync(Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        await using var conn = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO projects (id, name, created_at, user_hash_key, environments, members)
            VALUES (@id, @name, @created, @hk, @envs::jsonb, @members::jsonb)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, environments = excluded.environments, members = excluded.members
            """, conn, tx))
        {
            upsert.Parameters.AddWithValue("id", project.Id.Value);
            upsert.Parameters.AddWithValue("name", project.Name);
            upsert.Parameters.AddWithValue("created", project.CreatedAt);
            upsert.Parameters.AddWithValue("hk", project.UserHashKey);
            upsert.Parameters.AddWithValue("envs", Json.Write(project.Environments.Select(e => new EnvRow(e.Id.Value, e.Name, e.Kind))));
            upsert.Parameters.AddWithValue("members", Json.Write(project.Members.Select(m => new MemberRow(m.UserId.Value, m.Role))));
            await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var k in project.ApiKeys)
        {
            await using var key = new NpgsqlCommand("""
                INSERT INTO api_keys (id, project_id, environment_id, prefix, hash, created_at, revoked_at, expires_at)
                VALUES (@id, @p, @e, @prefix, @hash, @created, @revoked, @expires)
                ON CONFLICT (id) DO UPDATE SET revoked_at = excluded.revoked_at, expires_at = excluded.expires_at
                """, conn, tx);
            key.Parameters.AddWithValue("id", k.Id.Value);
            key.Parameters.AddWithValue("p", project.Id.Value);
            key.Parameters.AddWithValue("e", k.EnvironmentId.Value);
            key.Parameters.AddWithValue("prefix", k.Prefix);
            key.Parameters.AddWithValue("hash", k.Hash);
            key.Parameters.AddWithValue("created", k.CreatedAt);
            key.Parameters.AddWithValue("revoked", (object?)k.RevokedAt ?? DBNull.Value);
            key.Parameters.AddWithValue("expires", (object?)k.ExpiresAt ?? DBNull.Value);
            await key.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var clear = new NpgsqlCommand("DELETE FROM project_members WHERE project_id = @p", conn, tx))
        {
            clear.Parameters.AddWithValue("p", project.Id.Value);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var m in project.Members)
        {
            await using var add = new NpgsqlCommand("INSERT INTO project_members (project_id, user_id) VALUES (@p, @u)", conn, tx);
            add.Parameters.AddWithValue("p", project.Id.Value);
            add.Parameters.AddWithValue("u", m.UserId.Value);
            await add.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<Project?> LoadAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        Guid id; string name, hashKey, envs, members; DateTimeOffset created;
        await using (var cmd = db.CreateCommand(sql))
        {
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            (id, name, created, hashKey, envs, members) = (reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
        }

        var keys = new List<ApiKey>();
        await using (var cmd = db.CreateCommand("SELECT id, environment_id, prefix, hash, created_at, revoked_at, expires_at FROM api_keys WHERE project_id = @p"))
        {
            cmd.Parameters.AddWithValue("p", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                keys.Add(ApiKey.Restore(new ApiKeyId(reader.GetGuid(0)), new EnvironmentId(reader.GetGuid(1)), reader.GetString(2), reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        return Project.Restore(new ProjectId(id), name, created, hashKey,
            Json.Read<List<EnvRow>>(envs).Select(e => (new EnvironmentId(e.Id), e.Name, e.Kind)),
            keys,
            Json.Read<List<MemberRow>>(members).Select(m => new Membership(new UserId(m.UserId), m.Role)));
    }
}

public sealed class PostgresGraphStore(NpgsqlDataSource db) : IGraphStore
{
    private sealed record Body(IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges, IReadOnlyList<SecurityFinding> Findings);

    public async Task SaveAsync(CodeGraphSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var cmd = db.CreateCommand("INSERT INTO graph_snapshots (project_id, commit, indexed_at, body) VALUES (@p, @c, @at, @b::jsonb)");
        cmd.Parameters.AddWithValue("p", snapshot.ProjectId.Value);
        cmd.Parameters.AddWithValue("c", snapshot.Commit);
        cmd.Parameters.AddWithValue("at", snapshot.IndexedAt);
        cmd.Parameters.AddWithValue("b", Json.Write(new Body(snapshot.Nodes, snapshot.Edges, snapshot.Findings)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<CodeGraphSnapshot?> LatestAsync(ProjectId project, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT commit, indexed_at, body FROM graph_snapshots WHERE project_id = @p ORDER BY indexed_at DESC LIMIT 1");
        cmd.Parameters.AddWithValue("p", project.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var body = Json.Read<Body>(reader.GetString(2));
        return CodeGraphSnapshot.Create(project, reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), body.Nodes, body.Edges, body.Findings);
    }

    public async Task SaveExplanationsAsync(ProjectId project, IReadOnlyList<Explanation> explanations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(explanations);
        foreach (var e in explanations)
        {
            await using var cmd = db.CreateCommand("""
                INSERT INTO explanations (project_id, node_id, code_hash, text, written_at) VALUES (@p, @n, @h, @t, @w)
                ON CONFLICT (project_id, node_id) DO UPDATE SET code_hash = excluded.code_hash, text = excluded.text, written_at = excluded.written_at
                """);
            cmd.Parameters.AddWithValue("p", project.Value);
            cmd.Parameters.AddWithValue("n", e.NodeId);
            cmd.Parameters.AddWithValue("h", e.CodeHash);
            cmd.Parameters.AddWithValue("t", e.Text);
            cmd.Parameters.AddWithValue("w", e.WrittenAt);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<string, Explanation>> ExplanationsAsync(ProjectId project, CancellationToken ct)
    {
        var map = new Dictionary<string, Explanation>(StringComparer.Ordinal);
        await using var cmd = db.CreateCommand("SELECT node_id, code_hash, text, written_at FROM explanations WHERE project_id = @p");
        cmd.Parameters.AddWithValue("p", project.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            map[reader.GetString(0)] = new Explanation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        }

        return map;
    }
}

public sealed class PostgresActivityStore(NpgsqlDataSource db) : IActivityStore
{
    public async Task AppendAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        await using var batch = db.CreateBatch();
        foreach (var e in events)
        {
            var cmd = new NpgsqlBatchCommand("INSERT INTO activity (project_id, session_id, at, kind, summary, node_ids) VALUES (@p, @s, @at, @k, @sum, @n::jsonb)");
            cmd.Parameters.AddWithValue("p", e.ProjectId.Value);
            cmd.Parameters.AddWithValue("s", e.SessionId);
            cmd.Parameters.AddWithValue("at", e.At);
            cmd.Parameters.AddWithValue("k", e.Kind.ToString());
            cmd.Parameters.AddWithValue("sum", e.Summary);
            cmd.Parameters.AddWithValue("n", Json.Write(e.NodeIds));
            batch.BatchCommands.Add(cmd);
        }

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActivityEvent>> RangeAsync(ProjectId project, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        var list = new List<ActivityEvent>();
        await using var cmd = db.CreateCommand("SELECT session_id, at, kind, summary, node_ids FROM activity WHERE project_id = @p AND at BETWEEN @a AND @b ORDER BY at");
        cmd.Parameters.AddWithValue("p", project.Value);
        cmd.Parameters.AddWithValue("a", since);
        cmd.Parameters.AddWithValue("b", until);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ActivityEvent.Record(project, reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), Enum.Parse<ActivityKind>(reader.GetString(2)), reader.GetString(3), Json.Read<List<string>>(reader.GetString(4))));
        }

        return list;
    }
}

public sealed class PostgresDecisionRepository(NpgsqlDataSource db) : IDecisionRepository
{
    private sealed record Row(Guid Id, Guid ProjectId, DecisionArea Area, string Title, string Context, IReadOnlyList<DecisionOption> Options, string RecommendedKey,
        Guid? Supersedes, DateTimeOffset ProposedAt, DecisionStatus Status, string? ChosenKey, string? ResolvedBy, DateTimeOffset? ResolvedAt, string? Note);

    public async Task<Decision?> GetAsync(DecisionId id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT body FROM decisions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id.Value);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string body ? FromRow(Json.Read<Row>(body)) : null;
    }

    public async Task<IReadOnlyList<Decision>> ListAsync(ProjectId project, CancellationToken ct)
    {
        var list = new List<Decision>();
        await using var cmd = db.CreateCommand("SELECT body FROM decisions WHERE project_id = @p ORDER BY proposed_at");
        cmd.Parameters.AddWithValue("p", project.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(FromRow(Json.Read<Row>(reader.GetString(0))));
        }

        return list;
    }

    public async Task SaveAsync(Decision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var row = new Row(decision.Id.Value, decision.ProjectId.Value, decision.Area, decision.Title, decision.Context, decision.Options, decision.RecommendedKey,
            decision.Supersedes?.Value, decision.ProposedAt, decision.Status, decision.ChosenKey, decision.ResolvedBy?.Value, decision.ResolvedAt, decision.Note);
        await using var cmd = db.CreateCommand("INSERT INTO decisions (id, project_id, proposed_at, body) VALUES (@id, @p, @at, @b::jsonb) ON CONFLICT (id) DO UPDATE SET body = excluded.body");
        cmd.Parameters.AddWithValue("id", row.Id);
        cmd.Parameters.AddWithValue("p", row.ProjectId);
        cmd.Parameters.AddWithValue("at", row.ProposedAt);
        cmd.Parameters.AddWithValue("b", Json.Write(row));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        decision.ClearDomainEvents();
    }

    private static Decision FromRow(Row r) => Decision.Restore(new DecisionId(r.Id), new ProjectId(r.ProjectId), r.Area, r.Title, r.Context, r.Options, r.RecommendedKey,
        r.Supersedes is { } s ? new DecisionId(s) : null, r.ProposedAt, r.Status, r.ChosenKey, r.ResolvedBy is { } u ? new UserId(u) : null, r.ResolvedAt, r.Note);
}

public sealed class PostgresAuditLog(NpgsqlDataSource db) : IAuditLog
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var cmd = db.CreateCommand("INSERT INTO audit_log (project_id, actor, action, detail, at) VALUES (@p, @a, @x, @d, @t)");
        cmd.Parameters.AddWithValue("p", entry.ProjectId.Value);
        cmd.Parameters.AddWithValue("a", entry.Actor.Value);
        cmd.Parameters.AddWithValue("x", entry.Action);
        cmd.Parameters.AddWithValue("d", entry.Detail);
        cmd.Parameters.AddWithValue("t", entry.At);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(ProjectId project, CancellationToken ct)
    {
        var list = new List<AuditEntry>();
        await using var cmd = db.CreateCommand("SELECT actor, action, detail, at FROM audit_log WHERE project_id = @p ORDER BY id");
        cmd.Parameters.AddWithValue("p", project.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AuditEntry(project, new UserId(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return list;
    }
}

/// <summary>Traffic rows with the columns replay filters on. Rows older than the live window move to the archive (next step), never deleted here.</summary>
public sealed class PostgresTrafficStore(NpgsqlDataSource db) : ITrafficStore
{
    public async Task AppendAsync(IReadOnlyList<RequestTrace> traces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(traces);
        if (traces.Count == 0)
        {
            return;
        }

        await using var conn = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY traffic (project_id, environment_id, trace_id, at, method, route, status, duration_ms, guards, user_hash, error_type) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false);
        foreach (var t in traces)
        {
            await writer.StartRowAsync(ct).ConfigureAwait(false);
            await writer.WriteAsync(t.ProjectId.Value, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
            await writer.WriteAsync(t.EnvironmentId.Value, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
            await writer.WriteAsync(t.TraceId, NpgsqlDbType.Char, ct).ConfigureAwait(false);
            await writer.WriteAsync(t.At, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await writer.WriteAsync(t.Method, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(t.Route, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync((short)t.Status, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
            await writer.WriteAsync(t.DurationMs, NpgsqlDbType.Double, ct).ConfigureAwait(false);
            await writer.WriteAsync(t.Guards.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await (t.UserHash is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(t.UserHash, NpgsqlDbType.Char, ct)).ConfigureAwait(false);
            await (t.ErrorType is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(t.ErrorType, NpgsqlDbType.Text, ct)).ConfigureAwait(false);
        }

        await writer.CompleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RequestTrace>> RangeAsync(ProjectId project, DateTimeOffset since, DateTimeOffset until, string? userHash, int limit, CancellationToken ct)
    {
        var list = new List<RequestTrace>();
        await using var cmd = db.CreateCommand("""
            SELECT environment_id, trace_id, at, method, route, status, duration_ms, guards, user_hash, error_type
            FROM traffic WHERE project_id = @p AND at BETWEEN @a AND @b AND (@u::char(32) IS NULL OR user_hash = @u)
            ORDER BY at LIMIT @n
            """);
        cmd.Parameters.AddWithValue("p", project.Value);
        cmd.Parameters.AddWithValue("a", since);
        cmd.Parameters.AddWithValue("b", until);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Char) { Value = (object?)userHash ?? DBNull.Value });
        cmd.Parameters.AddWithValue("n", Math.Max(1, limit));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(RequestTrace.Record(project, new EnvironmentId(reader.GetGuid(0)), reader.GetString(1).Trim(), reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt16(5), reader.GetDouble(6), reader.GetFieldValue<string[]>(7),
                reader.IsDBNull(8) ? null : reader.GetString(8).Trim(), reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return list;
    }
}
