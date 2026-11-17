using System.Threading.Channels;
using ErpAgent.Api.Data;
using ErpAgent.Core;
using Microsoft.Extensions.AI;

namespace ErpAgent.Api;

public sealed class RunQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid runId) => _channel.Writer.TryWrite(runId);
}

/// <summary>
/// Advances runs in the background. A run is processed until it completes, fails or waits for a human;
/// then its state is saved and the worker moves on. On startup, runs that were mid-flight are picked up again.
/// </summary>
/// <remarks>Single instance for now. Multiple instances would claim runs with a lease (see roadmap).</remarks>
public sealed class RunWorker(
    RunQueue queue,
    RunStore store,
    IChatClient chat,
    IToolProvider toolProvider,
    ToolPolicy policy,
    IConfiguration config,
    ILogger<RunWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var id in await store.GetRunnableIdsAsync(stoppingToken))
        {
            log.LogInformation("Resuming run {RunId} after restart", id);
            queue.Enqueue(id);
        }

        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Run {RunId} could not be processed", id);
            }
        }
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        var run = await store.GetAsync(id, ct);
        if (run is null || run.State.Status != RunStatus.Running)
        {
            return;
        }

        var state = run.State;
        var callsBefore = state.Calls.Count;
        var pendingBefore = state.Pending.Select(p => p.CallId).ToHashSet();

        try
        {
            var tools = policy.Visible(await toolProvider.GetToolsAsync(ct));
            var maxSteps = int.TryParse(config["Agent:MaxSteps"], out var steps) ? steps : 12;
            var engine = new AgentEngine(chat, tools, policy.RequiresApproval, maxSteps);
            await engine.AdvanceAsync(state, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Run {RunId} failed", id);
            state.Status = RunStatus.Failed;
            state.Answer = $"Run failed: {ex.Message}";
        }

        var newApprovals = state.Pending.Where(p => p.IsUndecided && !pendingBefore.Contains(p.CallId)).ToList();
        var events = new List<(string Kind, object Data)>();
        foreach (var call in state.Calls.Skip(callsBefore))
        {
            events.Add(("tool.call", new { call.Tool, call.Decision, call.RequiredApproval, arguments = call.Arguments }));
        }
        foreach (var pending in newApprovals)
        {
            events.Add(("approval.requested", new { pending.CallId, pending.Tool, arguments = pending.Arguments }));
        }
        events.Add(state.Status switch
        {
            RunStatus.Completed => ("run.completed", new { answer = state.Answer }),
            RunStatus.Failed => ("run.failed", new { answer = state.Answer, hitStepLimit = state.HitStepLimit }),
            RunStatus.AwaitingApproval => ("run.waiting", new { pending = newApprovals.Count }),
            _ => ("run.progress", new { status = state.Status.ToString() }),
        });

        if (!await store.SaveAsync(id, run.Row.Version, state, newApprovals, events, ct))
        {
            log.LogWarning("Run {RunId} changed while it was processed; retrying", id);
            queue.Enqueue(id);
            return;
        }

        if (state.Status == RunStatus.Running)
        {
            queue.Enqueue(id);
        }
    }
}
