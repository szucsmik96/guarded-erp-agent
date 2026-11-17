using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static ErpAgent.Api.Tests.ScriptedChatClient;

namespace ErpAgent.Api.Tests;

public sealed class ApprovalFlowTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Write_waits_for_approval_then_completes()
    {
        var tools = new FakeErpTools();
        await using var api = new ApiFactory(db.ConnectionString,
            new ScriptedChatClient(Call("read_stock", "c1"), Call("create_order", "c2"), Say("Order placed.")), tools);
        var client = api.CreateClient();

        var runId = await ApiCalls.StartRunAsync(client, "order 2 chairs");
        await ApiCalls.WaitForStatusAsync(client, runId, "AwaitingApproval");
        Assert.Equal(0, tools.Orders);

        var approval = await ApiCalls.SinglePendingApprovalAsync(client, runId);
        Assert.Equal("create_order", approval.GetProperty("tool").GetString());

        using var approved = await ApiCalls.DecideAsync(client, approval.GetProperty("id").GetGuid(), "approve");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var run = await ApiCalls.WaitForStatusAsync(client, runId, "Completed");
        Assert.Equal("Order placed.", run.GetProperty("answer").GetString());
        Assert.Equal(1, tools.Orders);
        Assert.Equal($"{runId:N}:c2", Assert.Single(tools.RequestIds));

        var decisions = run.GetProperty("calls").EnumerateArray().Select(c => c.GetProperty("decision").GetString()).ToList();
        Assert.Equal(new[] { "auto", "approved" }, decisions);
    }

    [Fact]
    public async Task Rejected_write_never_runs()
    {
        var tools = new FakeErpTools();
        await using var api = new ApiFactory(db.ConnectionString,
            new ScriptedChatClient(Call("create_order"), Say("Understood, not ordering.")), tools);
        var client = api.CreateClient();

        var runId = await ApiCalls.StartRunAsync(client, "order it");
        await ApiCalls.WaitForStatusAsync(client, runId, "AwaitingApproval");
        var approval = await ApiCalls.SinglePendingApprovalAsync(client, runId);

        using var rejected = await ApiCalls.DecideAsync(client, approval.GetProperty("id").GetGuid(), "reject", "over budget");
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var run = await ApiCalls.WaitForStatusAsync(client, runId, "Completed");
        Assert.Equal(0, tools.Orders);
        Assert.Equal("denied", run.GetProperty("calls")[0].GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Approve_is_idempotent_and_the_opposite_decision_conflicts()
    {
        var tools = new FakeErpTools();
        await using var api = new ApiFactory(db.ConnectionString,
            new ScriptedChatClient(Call("create_order"), Say("done")), tools);
        var client = api.CreateClient();

        var runId = await ApiCalls.StartRunAsync(client, "order it");
        await ApiCalls.WaitForStatusAsync(client, runId, "AwaitingApproval");
        var approvalId = (await ApiCalls.SinglePendingApprovalAsync(client, runId)).GetProperty("id").GetGuid();

        var key = Guid.NewGuid().ToString();
        using var first = await ApiCalls.DecideAsync(client, approvalId, "approve", idempotencyKey: key);
        using var retry = await ApiCalls.DecideAsync(client, approvalId, "approve", idempotencyKey: key);
        using var again = await ApiCalls.DecideAsync(client, approvalId, "approve");
        using var opposite = await ApiCalls.DecideAsync(client, approvalId, "reject");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(),
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, opposite.StatusCode);

        await ApiCalls.WaitForStatusAsync(client, runId, "Completed");
        Assert.Equal(1, tools.Orders);
    }

    [Fact]
    public async Task Paused_run_survives_an_api_restart()
    {
        Guid runId;
        await using (var before = new ApiFactory(db.ConnectionString,
                         new ScriptedChatClient(Call("create_order", "c9")), new FakeErpTools()))
        {
            var client = before.CreateClient();
            runId = await ApiCalls.StartRunAsync(client, "order it");
            await ApiCalls.WaitForStatusAsync(client, runId, "AwaitingApproval");
        }

        // A new process: nothing in memory, only Postgres.
        var tools = new FakeErpTools();
        await using var after = new ApiFactory(db.ConnectionString, new ScriptedChatClient(Say("Done after restart.")), tools);
        var restarted = after.CreateClient();

        var approval = await ApiCalls.SinglePendingApprovalAsync(restarted, runId);
        using var approved = await ApiCalls.DecideAsync(restarted, approval.GetProperty("id").GetGuid(), "approve");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var run = await ApiCalls.WaitForStatusAsync(restarted, runId, "Completed");
        Assert.Equal("Done after restart.", run.GetProperty("answer").GetString());
        Assert.Equal(1, tools.Orders);
        Assert.Equal($"{runId:N}:c9", Assert.Single(tools.RequestIds));
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_request_is_rejected()
    {
        await using var api = new ApiFactory(db.ConnectionString, new ScriptedChatClient(Say("hi"), Say("hi")), new FakeErpTools());
        var client = api.CreateClient();
        var key = Guid.NewGuid().ToString();

        var first = await ApiCalls.StartRunAsync(client, "hello", key);

        using var replay = new HttpRequestMessage(HttpMethod.Post, "/runs") { Content = JsonContent.Create(new { message = "hello" }) };
        replay.Headers.Add("Idempotency-Key", key);
        using var replayed = await client.SendAsync(replay);
        Assert.Equal(first, (await replayed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        using var different = new HttpRequestMessage(HttpMethod.Post, "/runs") { Content = JsonContent.Create(new { message = "something else" }) };
        different.Headers.Add("Idempotency-Key", key);
        using var rejected = await client.SendAsync(different);
        Assert.Equal((HttpStatusCode)422, rejected.StatusCode);

        // Leave the database tidy: later tests start new API instances that resume unfinished runs.
        await ApiCalls.WaitForStatusAsync(client, first, "Completed");
    }

    [Fact]
    public async Task Unknown_run_and_approval_return_404()
    {
        await using var api = new ApiFactory(db.ConnectionString, new ScriptedChatClient(), new FakeErpTools());
        var client = api.CreateClient();

        using var run = await client.GetAsync($"/runs/{Guid.NewGuid()}");
        using var approval = await ApiCalls.DecideAsync(client, Guid.NewGuid(), "approve");

        Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, approval.StatusCode);
    }
}
