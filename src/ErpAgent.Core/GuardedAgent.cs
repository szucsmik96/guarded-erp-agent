using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ErpAgent.Core;

public sealed record ToolCallRecord(
    string Tool, IDictionary<string, object?> Arguments, bool RequiredApproval, string Decision, string? Outcome);

public sealed record AgentTurn(string Answer, IReadOnlyList<ToolCallRecord> Calls, bool HitStepLimit);

/// <summary>
/// A hand-written tool loop instead of UseFunctionInvocation(), so every call passes through
/// one place: policy check, optional human approval, audit, then execution.
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

    private readonly IChatClient _chat;
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly Dictionary<string, AIFunction> _byName;
    private readonly Func<AIFunction, bool> _requiresApproval;
    private readonly IApprovalGate _gate;
    private readonly AuditLog? _audit;
    private readonly int _maxSteps;
    private readonly Action<string>? _trace;
    private readonly List<ChatMessage> _history = [new(ChatRole.System, SystemPrompt)];

    public GuardedAgent(
        IChatClient chat,
        IReadOnlyList<AIFunction> tools,
        Func<AIFunction, bool> requiresApproval,
        IApprovalGate gate,
        AuditLog? audit = null,
        int maxSteps = 12,
        Action<string>? trace = null)
    {
        _chat = chat;
        _tools = tools;
        _byName = tools.ToDictionary(t => t.Name);
        _requiresApproval = requiresApproval;
        _gate = gate;
        _audit = audit;
        _maxSteps = maxSteps;
        _trace = trace;
    }

    public async Task<AgentTurn> RunTurnAsync(string userInput, CancellationToken ct = default)
    {
        _history.Add(new ChatMessage(ChatRole.User, userInput));
        var options = new ChatOptions { Tools = [.. _tools] };
        var records = new List<ToolCallRecord>();

        for (var step = 0; step < _maxSteps; step++)
        {
            var response = await _chat.GetResponseAsync(_history, options, ct);
            _history.AddMessages(response);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                return new AgentTurn(response.Text, records, HitStepLimit: false);
            }

            var results = new List<AIContent>();
            foreach (var call in calls)
            {
                var (result, record) = await ExecuteAsync(call, ct);
                records.Add(record);
                _audit?.Write(record);
                results.Add(result);
            }
            _history.Add(new ChatMessage(ChatRole.Tool, results));
        }

        return new AgentTurn(
            $"Stopped after {_maxSteps} steps without a final answer. Try a narrower request.", records, HitStepLimit: true);
    }

    private async Task<(FunctionResultContent Result, ToolCallRecord Record)> ExecuteAsync(
        FunctionCallContent call, CancellationToken ct)
    {
        var args = call.Arguments ?? new Dictionary<string, object?>();

        if (!_byName.TryGetValue(call.Name, out var tool))
        {
            return (new FunctionResultContent(call.CallId, $"Error: there is no tool named '{call.Name}'."),
                    new ToolCallRecord(call.Name, args, false, Decisions.UnknownTool, null));
        }

        var needsApproval = _requiresApproval(tool);
        _trace?.Invoke($"-> {tool.Name} {JsonSerializer.Serialize(args)}{(needsApproval ? "  [needs approval]" : "")}");

        if (needsApproval)
        {
            var decision = await _gate.RequestAsync(tool.Name, args, ct);
            if (!decision.Approved)
            {
                return (new FunctionResultContent(call.CallId,
                            $"DENIED by the user. Reason: {decision.Reason ?? "none given"}. " +
                            "Do not retry this action; ask the user how to proceed."),
                        new ToolCallRecord(tool.Name, args, true, Decisions.Denied, decision.Reason));
            }
        }

        try
        {
            var result = await tool.InvokeAsync(new AIFunctionArguments(args), ct);
            return (new FunctionResultContent(call.CallId, result),
                    new ToolCallRecord(tool.Name, args, needsApproval,
                        needsApproval ? Decisions.Approved : Decisions.Auto, JsonSerializer.Serialize(result)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (new FunctionResultContent(call.CallId, $"Error while running {tool.Name}: {ex.Message}"),
                    new ToolCallRecord(tool.Name, args, needsApproval, Decisions.Error, ex.Message));
        }
    }
}
