using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace ErpAgent.Core;

public enum RunStatus
{
    /// <summary>Created, no user message yet.</summary>
    Idle,
    Running,
    AwaitingApproval,
    Completed,
    Failed,
}

/// <summary>A tool call the model asked for, waiting to be executed (and, if needed, approved).</summary>
public sealed class PendingCall
{
    public required string CallId { get; init; }

    public required string Tool { get; init; }

    public Dictionary<string, object?> Arguments { get; init; } = new();

    public bool NeedsApproval { get; init; }

    /// <summary>null until a human decides.</summary>
    public bool? Approved { get; set; }

    public string? Reason { get; set; }

    public string? DecidedBy { get; set; }

    [JsonIgnore]
    public bool IsUndecided => NeedsApproval && Approved is null;
}

/// <summary>
/// The complete, serializable state of one agent conversation. Everything needed to resume lives here,
/// so a run can pause for approval, be stored, and continue in another process after a restart.
/// </summary>
public sealed class AgentRunState
{
    public Guid RunId { get; init; } = Guid.NewGuid();

    public List<ChatMessage> Messages { get; init; } = new();

    public List<PendingCall> Pending { get; init; } = new();

    public List<ToolCallRecord> Calls { get; init; } = new();

    public RunStatus Status { get; set; } = RunStatus.Idle;

    /// <summary>Model calls in the current turn.</summary>
    public int Steps { get; set; }

    public bool HitStepLimit { get; set; }

    public string? Answer { get; set; }

    public static AgentRunState Create(string systemPrompt, Guid? runId = null)
    {
        var state = new AgentRunState { RunId = runId ?? Guid.NewGuid() };
        state.Messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        return state;
    }

    /// <summary>Starts a new turn. Only allowed when the previous turn has finished.</summary>
    public void AddUserMessage(string text)
    {
        if (Status is RunStatus.Running or RunStatus.AwaitingApproval)
        {
            throw new InvalidOperationException($"The run is {Status}; wait for it to finish before sending a new message.");
        }

        Messages.Add(new ChatMessage(ChatRole.User, text));
        Status = RunStatus.Running;
        Steps = 0;
        HitStepLimit = false;
        Answer = null;
    }

    /// <summary>Records a human decision. When nothing is left undecided, the run can continue.</summary>
    public void Decide(string callId, bool approved, string? reason = null, string? decidedBy = null)
    {
        if (Status != RunStatus.AwaitingApproval)
        {
            throw new InvalidOperationException($"The run is {Status}, not awaiting approval.");
        }

        var call = Pending.FirstOrDefault(p => p.CallId == callId && p.NeedsApproval)
                   ?? throw new KeyNotFoundException($"No call '{callId}' is waiting for approval.");
        if (call.Approved is not null)
        {
            throw new InvalidOperationException($"Call '{callId}' was already decided.");
        }

        call.Approved = approved;
        call.Reason = reason;
        call.DecidedBy = decidedBy;

        if (!Pending.Any(p => p.IsUndecided))
        {
            Status = RunStatus.Running;
        }
    }
}

public static class AgentStateSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(AgentRunState state) => JsonSerializer.Serialize(state, Options);

    public static AgentRunState Deserialize(string json) =>
        JsonSerializer.Deserialize<AgentRunState>(json, Options)
        ?? throw new InvalidOperationException("Stored run state is empty.");

    private static JsonSerializerOptions CreateOptions()
    {
        // AIJsonUtilities knows the polymorphic AIContent types (text, function call/result, ...).
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                AIJsonUtilities.DefaultOptions.TypeInfoResolver, new DefaultJsonTypeInfoResolver()),

            // Postgres jsonb does not keep key order, so "$type" may no longer be the first property.
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
/// The agent loop as a resumable state machine. <see cref="AdvanceAsync"/> runs until the turn completes,
/// fails, or a tool call needs a human; it never blocks waiting for approval.
/// </summary>
public sealed class AgentEngine
{
    /// <summary>Tools that declare this parameter get a stable id per call, so a retried write is applied once.</summary>
    public const string RequestIdArgument = "requestId";

    private readonly IChatClient _chat;
    private readonly Dictionary<string, AIFunction> _byName;
    private readonly ChatOptions _options;
    private readonly Func<AIFunction, bool> _requiresApproval;
    private readonly int _maxSteps;
    private readonly Action<string>? _trace;
    private readonly Action<ToolCallRecord>? _onCall;

    public AgentEngine(
        IChatClient chat,
        IReadOnlyList<AIFunction> tools,
        Func<AIFunction, bool> requiresApproval,
        int maxSteps = 12,
        Action<string>? trace = null,
        Action<ToolCallRecord>? onCall = null)
    {
        _chat = chat;
        _byName = tools.ToDictionary(t => t.Name);
        _options = new ChatOptions { Tools = [.. tools] };
        _requiresApproval = requiresApproval;
        _maxSteps = maxSteps;
        _trace = trace;
        _onCall = onCall;
    }

    public async Task AdvanceAsync(AgentRunState state, CancellationToken ct = default)
    {
        while (state.Status == RunStatus.Running)
        {
            if (state.Pending.Count > 0)
            {
                if (state.Pending.Any(p => p.IsUndecided))
                {
                    state.Status = RunStatus.AwaitingApproval;
                    return;
                }

                await ExecutePendingAsync(state, ct);
                continue;
            }

            if (state.Steps >= _maxSteps)
            {
                state.Status = RunStatus.Failed;
                state.HitStepLimit = true;
                state.Answer = $"Stopped after {_maxSteps} steps without a final answer. Try a narrower request.";
                return;
            }

            state.Steps++;
            var response = await _chat.GetResponseAsync(state.Messages, _options, ct);
            state.Messages.AddMessages(response);

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                state.Answer = response.Text;
                state.Status = RunStatus.Completed;
                return;
            }

            foreach (var call in calls)
            {
                var args = call.Arguments is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(call.Arguments);
                var needsApproval = _byName.TryGetValue(call.Name, out var tool) && _requiresApproval(tool);
                state.Pending.Add(new PendingCall
                {
                    CallId = call.CallId,
                    Tool = call.Name,
                    Arguments = args,
                    NeedsApproval = needsApproval,
                });
                _trace?.Invoke($"-> {call.Name} {JsonSerializer.Serialize(args)}{(needsApproval ? "  [needs approval]" : "")}");
            }
        }
    }

    private async Task ExecutePendingAsync(AgentRunState state, CancellationToken ct)
    {
        var results = new List<AIContent>();
        foreach (var pending in state.Pending)
        {
            var (result, record) = await ExecuteOneAsync(state, pending, ct);
            state.Calls.Add(record);
            _onCall?.Invoke(record);
            results.Add(result);
        }

        state.Messages.Add(new ChatMessage(ChatRole.Tool, results));
        state.Pending.Clear();
    }

    private async Task<(FunctionResultContent Result, ToolCallRecord Record)> ExecuteOneAsync(
        AgentRunState state, PendingCall call, CancellationToken ct)
    {
        if (!_byName.TryGetValue(call.Tool, out var tool))
        {
            return (new FunctionResultContent(call.CallId, $"Error: there is no tool named '{call.Tool}'."),
                    new ToolCallRecord(call.Tool, call.Arguments, false, GuardedAgent.Decisions.UnknownTool, null));
        }

        if (call.NeedsApproval && call.Approved == false)
        {
            return (new FunctionResultContent(call.CallId,
                        $"DENIED by the user. Reason: {call.Reason ?? "none given"}. " +
                        "Do not retry this action; ask the user how to proceed."),
                    new ToolCallRecord(tool.Name, call.Arguments, true, GuardedAgent.Decisions.Denied, call.Reason));
        }

        var args = new AIFunctionArguments(call.Arguments);
        if (AcceptsRequestId(tool))
        {
            // Stable across retries: if the process dies after the write but before the state is saved,
            // re-running this call is recognised by the server and not applied twice.
            args[RequestIdArgument] = $"{state.RunId:N}:{call.CallId}";
        }

        var decision = call.NeedsApproval ? GuardedAgent.Decisions.Approved : GuardedAgent.Decisions.Auto;
        try
        {
            var result = await tool.InvokeAsync(args, ct);
            return (new FunctionResultContent(call.CallId, result),
                    new ToolCallRecord(tool.Name, call.Arguments, call.NeedsApproval, decision, JsonSerializer.Serialize(result)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (new FunctionResultContent(call.CallId, $"Error while running {tool.Name}: {ex.Message}"),
                    new ToolCallRecord(tool.Name, call.Arguments, call.NeedsApproval, GuardedAgent.Decisions.Error, ex.Message));
        }
    }

    private static bool AcceptsRequestId(AIFunction tool) =>
        tool.JsonSchema.ValueKind == JsonValueKind.Object
        && tool.JsonSchema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(RequestIdArgument, out _);
}
