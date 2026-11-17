using Dapper;
using Npgsql;

namespace ErpAgent.Api.Data;

public sealed class Db(string connectionString)
{
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>
    /// Applies versioned SQL migrations in one transaction. The advisory lock makes this safe when
    /// several instances start at the same time: one migrates, the others wait and then find nothing to do.
    /// </summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(727274)", transaction: tx);
        await c.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS schema_migrations (version int PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
            transaction: tx);

        var applied = (await c.QueryAsync<int>("SELECT version FROM schema_migrations", transaction: tx)).ToHashSet();
        foreach (var (version, sql) in Migrations.All)
        {
            if (applied.Contains(version))
            {
                continue;
            }

            await c.ExecuteAsync(sql, transaction: tx);
            await c.ExecuteAsync("INSERT INTO schema_migrations (version) VALUES (@version)", new { version }, tx);
        }

        await tx.CommitAsync(ct);
    }
}

internal static class Migrations
{
    public static readonly (int Version, string Sql)[] All =
    [
        (1, """
            CREATE TABLE runs (
                id         uuid PRIMARY KEY,
                status     text NOT NULL,
                state      jsonb NOT NULL,
                version    int NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX ix_runs_status ON runs (status);

            CREATE TABLE approvals (
                id         uuid PRIMARY KEY,
                run_id     uuid NOT NULL REFERENCES runs (id),
                call_id    text NOT NULL,
                tool       text NOT NULL,
                arguments  jsonb NOT NULL,
                status     text NOT NULL,
                reason     text NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                decided_at timestamptz NULL,
                UNIQUE (run_id, call_id)
            );
            CREATE INDEX ix_approvals_status ON approvals (status);

            CREATE TABLE run_events (
                id     bigserial PRIMARY KEY,
                run_id uuid NOT NULL REFERENCES runs (id),
                at     timestamptz NOT NULL DEFAULT now(),
                kind   text NOT NULL,
                data   jsonb NOT NULL
            );
            CREATE INDEX ix_run_events_run ON run_events (run_id, id);

            CREATE TABLE idempotency_keys (
                key         text PRIMARY KEY,
                fingerprint text NOT NULL,
                status      int NULL,
                body        jsonb NULL,
                created_at  timestamptz NOT NULL DEFAULT now()
            );
            """),
    ];
}
