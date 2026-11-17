using System.Text.Json;
using ErpAgent.Api.Data;
using ErpAgent.Core;

namespace ErpAgent.Api;

public sealed record CreateRunRequest(string Message);

public sealed record MessageRequest(string Message);

public sealed record DecisionRequest(string? Reason);

public static class Endpoints
{
    public static void MapAgentApi(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        var runs = app.MapGroup("/runs").WithTags("Runs");

        runs.MapPost("/", (CreateRunRequest req, HttpRequest http, RunStore store, RunQueue queue, IdempotencyStore idempotency, CancellationToken ct) =>
            idempotency.ExecuteAsync(http, req, async () =>
            {
                if (string.IsNullOrWhiteSpace(req.Message))
                {
                    return (StatusCodes.Status400BadRequest, (object)new { error = "message is required" });
                }

                var state = AgentRunState.Create(GuardedAgent.SystemPrompt);
                state.AddUserMessage(req.Message.Trim());
                await store.CreateAsync(state, req.Message.Trim(), ct);
                queue.Enqueue(state.RunId);
                return (StatusCodes.Status202Accepted, (object)new { id = state.RunId, status = state.Status });
            }, ct))
            .WithSummary("Start a run. Returns immediately; poll GET /runs/{id} or stream /runs/{id}/events.");

        runs.MapGet("/{id:guid}", async (Guid id, RunStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } run
                ? Results.Json(RunView(run), ApiJson.Options)
                : Results.NotFound())
            .WithSummary("Run status, answer, tool calls and pending approvals.");

        runs.MapPost("/{id:guid}/messages", async (Guid id, MessageRequest req, RunStore store, RunQueue queue, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
            {
                return Results.BadRequest(new { error = "message is required" });
            }

            var (kind, run) = await store.AppendUserMessageAsync(id, req.Message.Trim(), ct);
            switch (kind)
            {
                case AppendKind.NotFound:
                    return Results.NotFound();
                case AppendKind.Busy:
                    return Results.Json(new { error = $"Run is {run!.State.Status}; wait for it to finish." }, ApiJson.Options, statusCode: 409);
                default:
                    queue.Enqueue(id);
                    return Results.Json(new { id, status = run!.State.Status }, ApiJson.Options, statusCode: 202);
            }
        })
        .WithSummary("Send a follow-up message once the previous turn has finished.");

        runs.MapGet("/{id:guid}/events", StreamEventsAsync)
            .WithSummary("Server-Sent Events: tool calls, approval requests and decisions, as they happen. Supports Last-Event-ID.");

        var approvals = app.MapGroup("/approvals").WithTags("Approvals");

        approvals.MapGet("/", async (string? status, Guid? runId, RunStore store, CancellationToken ct) =>
            Results.Json((await store.ListApprovalsAsync(status, runId, ct)).Select(ApprovalView), ApiJson.Options))
            .WithSummary("List approvals, e.g. ?status=pending.");

        approvals.MapPost("/{id:guid}/approve", (Guid id, DecisionRequest? body, HttpRequest http, RunStore store, RunQueue queue, IdempotencyStore idempotency, CancellationToken ct) =>
            DecideAsync(id, approved: true, body, http, store, queue, idempotency, ct))
            .WithSummary("Approve a pending write. Idempotent; send an Idempotency-Key header to make retries safe.");

        approvals.MapPost("/{id:guid}/reject", (Guid id, DecisionRequest? body, HttpRequest http, RunStore store, RunQueue queue, IdempotencyStore idempotency, CancellationToken ct) =>
            DecideAsync(id, approved: false, body, http, store, queue, idempotency, ct))
            .WithSummary("Reject a pending write. The agent is told, with your reason, and must not retry.");
    }

    private static Task<IResult> DecideAsync(
        Guid id, bool approved, DecisionRequest? body, HttpRequest http, RunStore store, RunQueue queue,
        IdempotencyStore idempotency, CancellationToken ct) =>
        idempotency.ExecuteAsync(http, new { id, approved, reason = body?.Reason }, async () =>
        {
            var outcome = await store.DecideAsync(id, approved, body?.Reason, ct);
            switch (outcome.Kind)
            {
                case DecisionKind.NotFound:
                    return (StatusCodes.Status404NotFound, (object)new { error = "approval not found" });
                case DecisionKind.Conflict:
                    return (StatusCodes.Status409Conflict,
                        (object)new { error = $"Approval was already {outcome.Approval!.Status}.", approval = ApprovalView(outcome.Approval) });
                default:
                    if (outcome.RunResumed)
                    {
                        queue.Enqueue(outcome.Approval!.RunId);
                    }
                    return (StatusCodes.Status200OK, (object)ApprovalView(outcome.Approval!));
            }
        }, ct);

    private static async Task StreamEventsAsync(Guid id, HttpContext ctx, RunStore store, CancellationToken ct)
    {
        if (await store.GetAsync(id, ct) is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var last = long.TryParse(ctx.Request.Headers["Last-Event-ID"], out var fromHeader) ? fromHeader : 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = await store.GetEventsAsync(id, last, ct);
                foreach (var e in events)
                {
                    await ctx.Response.WriteAsync($"id: {e.Id}\nevent: {e.Kind}\ndata: {e.Data}\n\n", ct);
                    last = e.Id;
                }
                await ctx.Response.Body.FlushAsync(ct);

                // Stop at the end of a turn; clients reconnect with Last-Event-ID after a follow-up message.
                if (events.Count > 0 && events[^1].Kind is "run.completed" or "run.failed")
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away.
        }
    }

    private static object RunView(StoredRun run) => new
    {
        id = run.Row.Id,
        status = run.State.Status,
        answer = run.State.Answer,
        steps = run.State.Steps,
        hitStepLimit = run.State.HitStepLimit,
        createdAt = run.Row.CreatedAt,
        updatedAt = run.Row.UpdatedAt,
        calls = run.State.Calls.Select(c => new { c.Tool, c.Decision, c.RequiredApproval, arguments = c.Arguments }),
        pending = run.State.Pending.Where(p => p.IsUndecided).Select(p => new { p.CallId, p.Tool, arguments = p.Arguments }),
    };

    private static object ApprovalView(ApprovalRow a) => new
    {
        id = a.Id,
        runId = a.RunId,
        callId = a.CallId,
        tool = a.Tool,
        arguments = JsonDocument.Parse(a.Arguments).RootElement.Clone(),
        status = a.Status,
        reason = a.Reason,
        createdAt = a.CreatedAt,
        decidedAt = a.DecidedAt,
    };
}
