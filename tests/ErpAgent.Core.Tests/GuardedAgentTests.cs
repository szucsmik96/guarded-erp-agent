using Microsoft.Extensions.AI;
using static ErpAgent.Core.Tests.ScriptedChatClient;

namespace ErpAgent.Core.Tests;

public sealed class GuardedAgentTests
{
    private readonly FakeTools _tools = new();

    private GuardedAgent Agent(IChatClient chat, IApprovalGate gate, int maxSteps = 12) =>
        new(chat, _tools.All, FakeTools.Policy, gate, maxSteps: maxSteps);

    [Fact]
    public async Task Read_tool_runs_without_asking_the_human()
    {
        var gate = new RecordingGate(approve: false);
        var turn = await Agent(new ScriptedChatClient(Call("read_stock"), Say("5 in stock")), gate).RunTurnAsync("stock?");

        Assert.Equal(1, _tools.ReadCalls);
        Assert.Empty(gate.Requests);
        Assert.Equal(GuardedAgent.Decisions.Auto, Assert.Single(turn.Calls).Decision);
        Assert.Equal("5 in stock", turn.Answer);
    }

    [Fact]
    public async Task Denied_write_is_never_executed_and_the_model_is_told()
    {
        var chat = new ScriptedChatClient(Call("create_order"), Say("ok, not creating it"));
        var gate = new RecordingGate(approve: false);

        var turn = await Agent(chat, gate).RunTurnAsync("order it");

        Assert.Equal(0, _tools.WriteCalls);
        Assert.Equal(new[] { "create_order" }, gate.Requests);
        Assert.Equal(GuardedAgent.Decisions.Denied, Assert.Single(turn.Calls).Decision);

        var toolResult = chat.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("DENIED", toolResult.Result?.ToString());
    }

    [Fact]
    public async Task Approved_write_is_executed_once()
    {
        var gate = new RecordingGate(approve: true);
        var turn = await Agent(new ScriptedChatClient(Call("create_order"), Say("created")), gate).RunTurnAsync("order it");

        Assert.Equal(1, _tools.WriteCalls);
        Assert.Equal(GuardedAgent.Decisions.Approved, Assert.Single(turn.Calls).Decision);
    }

    [Fact]
    public async Task Unknown_tool_is_reported_back_not_thrown()
    {
        var turn = await Agent(new ScriptedChatClient(Call("drop_database"), Say("sorry")), new RecordingGate(true))
            .RunTurnAsync("hmm");

        Assert.Equal(GuardedAgent.Decisions.UnknownTool, Assert.Single(turn.Calls).Decision);
        Assert.Equal(0, _tools.WriteCalls);
    }

    [Fact]
    public async Task Step_limit_stops_a_looping_model()
    {
        var chat = new ScriptedChatClient(
            Call("read_stock", "c1"), Call("read_stock", "c2"), Call("read_stock", "c3"), Call("read_stock", "c4"));

        var turn = await Agent(chat, new RecordingGate(true), maxSteps: 3).RunTurnAsync("loop");

        Assert.True(turn.HitStepLimit);
        Assert.Equal(3, _tools.ReadCalls);
    }

    [Fact]
    public void Policy_fails_closed_for_tools_without_mcp_annotations()
    {
        var plain = AIFunctionFactory.Create(() => "x", "anything");

        Assert.True(ToolPolicy.RequiresApproval(plain));
    }
}
