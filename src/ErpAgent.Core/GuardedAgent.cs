using Microsoft.Extensions.AI;

namespace ErpAgent.Core;

public sealed record ToolCallRecord(
    string Tool, IDictionary<string, object?> Arguments, bool RequiredApproval, string Decision, string? Outcome);

public sealed record AgentTurn(string Answer, IReadOnlyList<ToolCallRecord> Calls, bool HitStepLimit);

/// <summary>
/// Synchronous wrapper over <see cref="AgentEngine"/> for the console and the evals: when the engine
/// pauses for approval, the gate is asked right away and the run continues in the same call.
/// The Agent API uses the engine directly and lets approvals arrive later, from another request.
/// </summary>
public sealed class GuardedAgent
{
    public const string SystemPrompt = """
        You are an order-desk assistant for a small distributor's ERP.
        Rules:
        - Never guess codes. Look up customers with find_customers and items with search_items.
        - Before create_sales_order, always call validate_sales_order and summarise lines, total and any issues.
        - If validation reports issues, explain them and suggest fixes (smaller quantity, alternative item). Do not create the order.
        - If the user denies an action, do not retry it. Ask what they want instead.
        - Never fill in requestId arguments; the system sets them.
        - Amounts are in EUR. Be concise.
        """;

    public static class Decisions
    {
        public const string Auto = "auto";
        public const string Approved = "approved";
        public const string Denied = "denied";
        public const string Error = "error";
        public const string UnknownTool = "unknown-tool";
    }

    private readonly AgentEngine _engine;
    private readonly IApprovalGate _gate;
    private readonly AgentRunState _state = AgentRunState.Create(SystemPrompt);

    public GuardedAgent(
        IChatClient chat,
        IReadOnlyList<AIFunction> tools,
        Func<AIFunction, bool> requiresApproval,
        IApprovalGate gate,
        AuditLog? audit = null,
        int maxSteps = 12,
        Action<string>? trace = null)
    {
        _gate = gate;
        Action<ToolCallRecord>? onCall = audit is null ? null : audit.Write;
        _engine = new AgentEngine(chat, tools, requiresApproval, maxSteps, trace, onCall);
    }

    public async Task<AgentTurn> RunTurnAsync(string userInput, CancellationToken ct = default)
    {
        _state.AddUserMessage(userInput);
        var before = _state.Calls.Count;

        await _engine.AdvanceAsync(_state, ct);
        while (_state.Status == RunStatus.AwaitingApproval)
        {
            foreach (var call in _state.Pending.Where(p => p.IsUndecided).ToList())
            {
                var decision = await _gate.RequestAsync(call.Tool, call.Arguments, ct);
                _state.Decide(call.CallId, decision.Approved, decision.Reason);
            }
            await _engine.AdvanceAsync(_state, ct);
        }

        return new AgentTurn(_state.Answer ?? "", _state.Calls.Skip(before).ToList(), _state.HitStepLimit);
    }
}
