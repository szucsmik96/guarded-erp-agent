using Microsoft.Extensions.AI;
using static ErpAgent.Core.Tests.ScriptedChatClient;

namespace ErpAgent.Core.Tests;

public sealed class AgentEngineTests
{
    private readonly FakeTools _tools = new();

    private AgentEngine Engine(IChatClient chat) => new(chat, _tools.All, FakeTools.Policy);

    [Fact]
    public async Task Write_pauses_the_run_instead_of_blocking()
    {
        var state = AgentRunState.Create("system");
        state.AddUserMessage("order it");

        await Engine(new ScriptedChatClient(Call("create_order"), Say("created"))).AdvanceAsync(state);

        Assert.Equal(RunStatus.AwaitingApproval, state.Status);
        Assert.Equal(0, _tools.WriteCalls);
        Assert.True(Assert.Single(state.Pending).IsUndecided);
    }

    [Fact]
    public async Task Paused_run_survives_serialization_and_resumes_in_a_new_engine()
    {
        var state = AgentRunState.Create("system");
        state.AddUserMessage("order it");
        var chat = new ScriptedChatClient(Call("create_order", "call-7"), Say("created"));
        await Engine(chat).AdvanceAsync(state);

        // Simulates a restart: only the JSON survives.
        var restored = AgentStateSerializer.Deserialize(AgentStateSerializer.Serialize(state));
        Assert.Equal(RunStatus.AwaitingApproval, restored.Status);
        Assert.Equal(state.RunId, restored.RunId);

        restored.Decide("call-7", approved: true);
        await Engine(chat).AdvanceAsync(restored);

        Assert.Equal(RunStatus.Completed, restored.Status);
        Assert.Equal("created", restored.Answer);
        Assert.Equal(1, _tools.WriteCalls);
        Assert.Equal(GuardedAgent.Decisions.Approved, Assert.Single(restored.Calls).Decision);
    }

    [Fact]
    public async Task State_still_deserializes_when_a_database_reorders_json_keys()
    {
        var state = AgentRunState.Create("system");
        state.AddUserMessage("order it");
        await Engine(new ScriptedChatClient(Call("create_order"))).AdvanceAsync(state);

        // Postgres jsonb sorts keys, which can move "$type" away from the front of each object.
        var reordered = SortKeys(System.Text.Json.Nodes.JsonNode.Parse(AgentStateSerializer.Serialize(state))!).ToJsonString();
        var restored = AgentStateSerializer.Deserialize(reordered);

        Assert.Equal(RunStatus.AwaitingApproval, restored.Status);
        Assert.Contains(restored.Messages.SelectMany(m => m.Contents), c => c is FunctionCallContent);
    }

    private static System.Text.Json.Nodes.JsonNode SortKeys(System.Text.Json.Nodes.JsonNode node) => node switch
    {
        System.Text.Json.Nodes.JsonObject obj => new System.Text.Json.Nodes.JsonObject(
            obj.OrderBy(p => p.Key.StartsWith('$') ? 1 : 0).ThenBy(p => p.Key)  // metadata keys like "$type" go last
               .Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : SortKeys(p.Value.DeepClone())))),
        System.Text.Json.Nodes.JsonArray arr => new System.Text.Json.Nodes.JsonArray(
            arr.Select(v => v is null ? null : SortKeys(v.DeepClone())).ToArray()),
        _ => node.DeepClone(),
    };

    [Fact]
    public async Task Rejected_call_is_reported_to_the_model_and_never_executed()
    {
        var state = AgentRunState.Create("system");
        state.AddUserMessage("order it");
        var chat = new ScriptedChatClient(Call("create_order"), Say("understood"));
        var engine = Engine(chat);
        await engine.AdvanceAsync(state);

        state.Decide("call-1", approved: false, reason: "too expensive");
        await engine.AdvanceAsync(state);

        Assert.Equal(0, _tools.WriteCalls);
        Assert.Equal(RunStatus.Completed, state.Status);
        var denial = chat.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("too expensive", denial.Result?.ToString());
    }

    [Fact]
    public async Task Tools_with_a_requestId_parameter_get_a_stable_id_per_call()
    {
        string? received = null;
        var writer = AIFunctionFactory.Create((string? requestId) => { received = requestId; return "ok"; }, "create_order");
        var engine = new AgentEngine(
            new ScriptedChatClient(Call("create_order", "call-3"), Say("done")), [writer], _ => false);

        var state = AgentRunState.Create("system");
        state.AddUserMessage("go");
        await engine.AdvanceAsync(state);

        Assert.Equal($"{state.RunId:N}:call-3", received);
    }

    [Fact]
    public void Deciding_outside_an_approval_wait_is_rejected()
    {
        var state = AgentRunState.Create("system");

        Assert.Throws<InvalidOperationException>(() => state.Decide("call-1", true));
    }

    [Fact]
    public void New_message_while_waiting_for_approval_is_rejected()
    {
        var state = AgentRunState.Create("system");
        state.AddUserMessage("first");

        Assert.Throws<InvalidOperationException>(() => state.AddUserMessage("second"));
    }
}
