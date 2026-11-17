using ErpAgent.Core;
using Microsoft.Extensions.AI;

namespace ErpAgent.Core.Tests;

/// <summary>Plays back a fixed list of model responses and records what the agent sent.</summary>
internal sealed class ScriptedChatClient(params ChatResponse[] script) : IChatClient
{
    private readonly Queue<ChatResponse> _script = new(script);

    public List<List<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        var next = _script.Count > 0 ? _script.Dequeue() : Say("done");
        return Task.FromResult(next);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse Call(string tool, string callId = "call-1", Dictionary<string, object?>? args = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args ?? new Dictionary<string, object?>())]));
}

internal sealed class RecordingGate(bool approve) : IApprovalGate
{
    public List<string> Requests { get; } = [];

    public Task<ApprovalDecision> RequestAsync(string toolName, IDictionary<string, object?> arguments, CancellationToken ct)
    {
        Requests.Add(toolName);
        return Task.FromResult(new ApprovalDecision(approve, approve ? null : "test says no"));
    }
}

/// <summary>Two fake tools: one read, one write. Counts real invocations.</summary>
internal sealed class FakeTools
{
    public int ReadCalls { get; private set; }

    public int WriteCalls { get; private set; }

    public IReadOnlyList<AIFunction> All =>
    [
        AIFunctionFactory.Create(() => { ReadCalls++; return "stock: 5"; }, "read_stock"),
        AIFunctionFactory.Create(() => { WriteCalls++; return "order 42 created"; }, "create_order"),
    ];

    public static bool Policy(AIFunction f) => f.Name.StartsWith("create_", StringComparison.Ordinal);
}
