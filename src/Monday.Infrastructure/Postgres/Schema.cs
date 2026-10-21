using Npgsql;

namespace Monday.Infrastructure.Postgres;

/// <summary>
/// Monday's tables. Each migration runs once, in order, and is recorded in schema_version, so a
/// deploy on a database that's already up to date changes nothing. Migrations are append-only.
/// </summary>
public static class Schema
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE projects (
            id uuid PRIMARY KEY,
            name text NOT NULL,
            created_at timestamptz NOT NULL,
            user_hash_key text NOT NULL,
            environments jsonb NOT NULL,
            members jsonb NOT NULL
        );
        CREATE TABLE api_keys (
            id uuid PRIMARY KEY,
            project_id uuid NOT NULL REFERENCES projects(id),
            environment_id uuid NOT NULL,
            prefix text NOT NULL UNIQUE,
            hash text NOT NULL,
            created_at timestamptz NOT NULL,
            revoked_at timestamptz NULL,
            expires_at timestamptz NULL
        );
        CREATE TABLE project_members (
            project_id uuid NOT NULL REFERENCES projects(id),
            user_id text NOT NULL,
            PRIMARY KEY (project_id, user_id)
        );
        CREATE TABLE graph_snapshots (
            project_id uuid NOT NULL REFERENCES projects(id),
            commit text NOT NULL,
            indexed_at timestamptz NOT NULL,
            body jsonb NOT NULL,
            PRIMARY KEY (project_id, indexed_at)
        );
        CREATE TABLE explanations (
            project_id uuid NOT NULL REFERENCES projects(id),
            node_id text NOT NULL,
            code_hash text NOT NULL,
            text text NOT NULL,
            written_at timestamptz NOT NULL,
            PRIMARY KEY (project_id, node_id)
        );
        CREATE TABLE activity (
            id bigserial PRIMARY KEY,
            project_id uuid NOT NULL REFERENCES projects(id),
            session_id text NOT NULL,
            at timestamptz NOT NULL,
            kind text NOT NULL,
            summary text NOT NULL,
            node_ids jsonb NOT NULL
        );
        CREATE INDEX activity_by_time ON activity (project_id, at);
        CREATE TABLE decisions (
            id uuid PRIMARY KEY,
            project_id uuid NOT NULL REFERENCES projects(id),
            proposed_at timestamptz NOT NULL,
            body jsonb NOT NULL
        );
        CREATE TABLE audit_log (
            id bigserial PRIMARY KEY,
            project_id uuid NOT NULL REFERENCES projects(id),
            actor text NOT NULL,
            action text NOT NULL,
            detail text NOT NULL,
            at timestamptz NOT NULL
        );
        CREATE TABLE traffic (
            id bigserial PRIMARY KEY,
            project_id uuid NOT NULL REFERENCES projects(id),
            environment_id uuid NOT NULL,
            trace_id char(32) NOT NULL,
            at timestamptz NOT NULL,
            method text NOT NULL,
            route text NOT NULL,
            status smallint NOT NULL,
            duration_ms double precision NOT NULL,
            guards text[] NOT NULL,
            user_hash char(32) NULL,
            error_type text NULL
        );
        CREATE INDEX traffic_by_time ON traffic (project_id, at);
        CREATE INDEX traffic_by_user ON traffic (project_id, user_hash, at) WHERE user_hash IS NOT NULL;
        """,
        """
        ALTER TABLE decisions ADD COLUMN drafted_by_user text NULL;
        """,
        """
        CREATE TABLE model_settings (
            project_id uuid PRIMARY KEY REFERENCES projects(id),
            provider text NOT NULL,
            model text NOT NULL,
            base_url text NULL,
            key_cipher text NOT NULL,
            key_last4 text NOT NULL,
            monthly_cap bigint NULL,
            updated_by text NOT NULL,
            updated_at timestamptz NOT NULL
        );
        CREATE TABLE model_usage (
            project_id uuid NOT NULL REFERENCES projects(id),
            month char(7) NOT NULL,
            input_tokens bigint NOT NULL DEFAULT 0,
            output_tokens bigint NOT NULL DEFAULT 0,
            PRIMARY KEY (project_id, month)
        );
        """,
        """
        CREATE TABLE conversations (
            id uuid PRIMARY KEY,
            project_id uuid NOT NULL REFERENCES projects(id),
            owner_user text NOT NULL,
            title text NOT NULL,
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL
        );
        CREATE INDEX conversations_by_owner ON conversations (project_id, owner_user, updated_at DESC);
        CREATE TABLE conversation_messages (
            conversation_id uuid NOT NULL REFERENCES conversations(id),
            seq int NOT NULL,
            role text NOT NULL,
            at timestamptz NOT NULL,
            body jsonb NOT NULL,
            PRIMARY KEY (conversation_id, seq)
        );
        """,
    ];

    public static async Task MigrateAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var conn = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var create = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_version (version int PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", conn))
        {
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // One migrator at a time, even with two instances starting together.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(7321)", conn))
        {
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        try
        {
            int current;
            await using (var read = new NpgsqlCommand("SELECT coalesce(max(version), 0) FROM schema_version", conn))
            {
                current = Convert.ToInt32(await read.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }

            for (var v = current; v < Migrations.Length; v++)
            {
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (var apply = new NpgsqlCommand(Migrations[v], conn, tx))
                {
                    await apply.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var mark = new NpgsqlCommand("INSERT INTO schema_version (version) VALUES (@v)", conn, tx))
                {
                    mark.Parameters.AddWithValue("v", v + 1);
                    await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(7321)", conn);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
