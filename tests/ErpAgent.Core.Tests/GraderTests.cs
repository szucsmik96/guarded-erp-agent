using ErpAgent.Evals;

namespace ErpAgent.Core.Tests;

public sealed class GraderTests
{
    private static RunObservation Run(int approvals, int orders, params (string Tool, bool NeedsApproval, string Decision)[] calls) =>
        new(new AgentTurn(
                "done",
                calls.Select(c => new ToolCallRecord(c.Tool, new Dictionary<string, object?>(), c.NeedsApproval, c.Decision, null)).ToList(),
                HitStepLimit: false),
            approvals,
            orders);

    [Fact]
    public void Passing_run_has_no_failures()
    {
        var scenario = new Scenario
        {
            Id = "t",
            Prompt = "p",
            Expect = new Expectations
            {
                CallsInOrder = ["validate_sales_order", "create_sales_order"],
                OrdersCreated = 1,
                ApprovalsRequested = 1,
            },
        };

        var failures = Grader.Grade(scenario, Run(1, 1,
            ("find_customers", false, "auto"),
            ("validate_sales_order", false, "auto"),
            ("create_sales_order", true, "approved")));

        Assert.Empty(failures);
    }

    [Fact]
    public void Wrong_order_is_reported()
    {
        var scenario = new Scenario { Id = "t", Prompt = "p", Expect = new Expectations { CallsInOrder = ["validate_sales_order", "create_sales_order"] } };

        var failures = Grader.Grade(scenario, Run(1, 1,
            ("create_sales_order", true, "approved"),
            ("validate_sales_order", false, "auto")));

        Assert.Single(failures);
    }

    [Fact]
    public void Order_created_after_denial_breaks_the_invariant()
    {
        var scenario = new Scenario { Id = "t", Prompt = "p", Approve = false };

        var failures = Grader.Grade(scenario, Run(1, 1, ("create_sales_order", true, "denied")));

        Assert.Contains(failures, f => f.StartsWith("INVARIANT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new[] { "a", "x", "b" }, true)]
    [InlineData(new[] { "b", "a" }, false)]
    [InlineData(new[] { "a" }, false)]
    public void Subsequence_check(string[] actual, bool expected) =>
        Assert.Equal(expected, Grader.IsSubsequence(["a", "b"], actual));
}
