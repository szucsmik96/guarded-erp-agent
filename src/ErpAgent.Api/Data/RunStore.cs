using System.Data;
using System.Text.Json;
using Dapper;
using ErpAgent.Core;
using Npgsql;

namespace ErpAgent.Api.Data;

public sealed class RunRow
{
    public Guid Id { get; set; }

    public string Status { get; set; } = "";

    public string State { get; set; } = "";

    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class ApprovalRow
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public string CallId { get; set; } = "";

    public string Tool { get; set; } = "";

    public string Arguments { get; set; } = "{}";

    public string Status { get; set; } = "";

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }
}

public sealed class EventRow
{
    public long Id { get; set; }

    public DateTime At { get; set; }

    public string Kind { get; set; } = "";

    public string Data { get; set; } = "{}";
}

public sealed record StoredRun(RunRow Row, AgentRunState State);

public enum DecisionKind { NotFound, Decided, AlreadyDecided, Conflict }

public sealed record DecisionOutcome(DecisionKind Kind, ApprovalRow? Approval, bool RunResumed);

public enum AppendKind { NotFound, Busy, Appended }

public static class ApprovalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>Postgres persistence for runs, approvals and the per-run event log. Every state change is one transaction.</summary>
public sealed class RunStore(Db db)
{
    private const string RunSelect =
        "SELECT id AS Id, status AS Status, state::text AS State, version AS Version, " +
        "created_at AS CreatedAt, updated_at AS UpdatedAt FROM runs";

    private const string ApprovalSelect =
        "SELECT id AS Id, run_id AS RunId, call_id AS CallId, tool AS Tool, arguments::text AS Arguments, " +
        "status AS Status, reason AS Reason, created_at AS CreatedAt, decided_at AS DecidedAt FROM approvals";

    public async Task CreateAsync(AgentRunState state, string message, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(
            "INSERT INTO runs (id, status, state, version) VALUES (@id, @status, @state::jsonb, 1)",
            new { id = state.RunId, status = state.Status.ToString(), state = AgentStateSerializer.Serialize(state) }, tx);
        await InsertEventAsync(c, tx, state.RunId, "run.created", new { message });
        await tx.CommitAsync(ct);
    }

    public async Task<StoredRun?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<RunRow>(RunSelect + " WHERE id = @id", new { id });
        return row is null ? null : new StoredRun(row, AgentStateSerializer.Deserialize(row.State));
    }

    public async Task<List<Guid>> GetRunnableIdsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<Guid>("SELECT id FROM runs WHERE status = @status ORDER BY created_at",
            new { status = RunStatus.Running.ToString() })).ToList();
    }

    /// <summary>Saves the result of one worker pass. Returns false if someone else changed the run meanwhile.</summary>
    public async Task<bool> SaveAsync(
        Guid id,
        int expectedVersion,
        AgentRunState state,
        IReadOnlyList<PendingCall> newApprovals,
        IReadOnlyList<(string Kind, object Data)> events,
        CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        var updated = await c.ExecuteAsync(
            "UPDATE runs SET status = @status, state = @state::jsonb, version = version + 1, updated_at = now() " +
            "WHERE id = @id AND version = @expectedVersion",
            new { id, expectedVersion, status = state.Status.ToString(), state = AgentStateSerializer.Serialize(state) }, tx);
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        foreach (var approval in newApprovals)
        {
            await c.ExecuteAsync(
                "INSERT INTO approvals (id, run_id, call_id, tool, arguments, status) " +
                "VALUES (@approvalId, @runId, @callId, @tool, @arguments::jsonb, @status) ON CONFLICT (run_id, call_id) DO NOTHING",
                new
                {
                    approvalId = Guid.NewGuid(),
                    runId = id,
                    callId = approval.CallId,
                    tool = approval.Tool,
                    arguments = JsonSerializer.Serialize(approval.Arguments, ApiJson.Options),
                    status = ApprovalStatus.Pending,
                }, tx);
        }

        foreach (var (kind, data) in events)
        {
            await InsertEventAsync(c, tx, id, kind, data);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<(AppendKind Kind, StoredRun? Run)> AppendUserMessageAsync(Guid id, string message, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        var row = await c.QuerySingleOrDefaultAsync<RunRow>(RunSelect + " WHERE id = @id FOR UPDATE", new { id }, tx);
        if (row is null)
        {
            return (AppendKind.NotFound, null);
        }

        var state = AgentStateSerializer.Deserialize(row.State);
        if (state.Status is RunStatus.Running or RunStatus.AwaitingApproval)
        {
            return (AppendKind.Busy, new StoredRun(row, state));
        }

        state.AddUserMessage(message);
        await UpdateStateAsync(c, tx, id, state);
        await InsertEventAsync(c, tx, id, "run.message", new { message });
        await tx.CommitAsync(ct);
        return (AppendKind.Appended, new StoredRun(row, state));
    }

    public async Task<List<ApprovalRow>> ListApprovalsAsync(string? status, Guid? runId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<ApprovalRow>(
            ApprovalSelect +
            " WHERE (@status::text IS NULL OR status = @status) AND (@runId::uuid IS NULL OR run_id = @runId) ORDER BY created_at",
            new { status, runId })).ToList();
    }

    /// <summary>
    /// Approve or reject, atomically: the approval row and the run are locked, the decision is applied to the
    /// stored run state, and the event is logged. Repeating the same decision is a no-op; the opposite one is a conflict.
    /// </summary>
    public async Task<DecisionOutcome> DecideAsync(Guid approvalId, bool approved, string? reason, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        var approval = await c.QuerySingleOrDefaultAsync<ApprovalRow>(
            ApprovalSelect + " WHERE id = @approvalId FOR UPDATE", new { approvalId }, tx);
        if (approval is null)
        {
            return new DecisionOutcome(DecisionKind.NotFound, null, false);
        }

        var wanted = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        if (approval.Status != ApprovalStatus.Pending)
        {
            await tx.RollbackAsync(ct);
            var kind = approval.Status == wanted ? DecisionKind.AlreadyDecided : DecisionKind.Conflict;
            return new DecisionOutcome(kind, approval, false);
        }

        var run = await c.QuerySingleAsync<RunRow>(RunSelect + " WHERE id = @id FOR UPDATE", new { id = approval.RunId }, tx);
        var state = AgentStateSerializer.Deserialize(run.State);
        state.Decide(approval.CallId, approved, reason);

        await UpdateStateAsync(c, tx, run.Id, state);
        await c.ExecuteAsync(
            "UPDATE approvals SET status = @wanted, reason = @reason, decided_at = now() WHERE id = @approvalId",
            new { wanted, reason, approvalId }, tx);
        await InsertEventAsync(c, tx, run.Id, "approval.decided",
            new { approvalId, approval.CallId, approval.Tool, decision = wanted, reason });
        await tx.CommitAsync(ct);

        approval.Status = wanted;
        approval.Reason = reason;
        approval.DecidedAt = DateTime.UtcNow;
        return new DecisionOutcome(DecisionKind.Decided, approval, state.Status == RunStatus.Running);
    }

    public async Task<List<EventRow>> GetEventsAsync(Guid runId, long afterId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<EventRow>(
            "SELECT id AS Id, at AS At, kind AS Kind, data::text AS Data FROM run_events " +
            "WHERE run_id = @runId AND id > @afterId ORDER BY id LIMIT 200",
            new { runId, afterId })).ToList();
    }

    private static Task UpdateStateAsync(NpgsqlConnection c, IDbTransaction tx, Guid id, AgentRunState state) =>
        c.ExecuteAsync(
            "UPDATE runs SET status = @status, state = @state::jsonb, version = version + 1, updated_at = now() WHERE id = @id",
            new { id, status = state.Status.ToString(), state = AgentStateSerializer.Serialize(state) }, tx);

    private static Task InsertEventAsync(NpgsqlConnection c, IDbTransaction tx, Guid runId, string kind, object data) =>
        c.ExecuteAsync(
            "INSERT INTO run_events (run_id, kind, data) VALUES (@runId, @kind, @data::jsonb)",
            new { runId, kind, data = JsonSerializer.Serialize(data, ApiJson.Options) }, tx);
}
