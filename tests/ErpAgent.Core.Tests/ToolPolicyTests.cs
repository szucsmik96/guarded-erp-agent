using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace ErpAgent.Core.Tests;

public sealed class ToolPolicyTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "x", name);

    [Fact]
    public void Default_policy_fails_closed_for_tools_without_mcp_annotations()
    {
        Assert.Equal(ToolDecision.NeedsApproval, ToolPolicy.Default.Evaluate(Tool("anything")));
    }

    [Fact]
    public void Deny_wins_over_everything()
    {
        var policy = new ToolPolicy(new ToolPolicyOptions
        {
            Deny = ["create_sales_order"],
            RequireApproval = ["create_sales_order"],
            AutoApprove = ["create_*"],
        });

        Assert.Equal(ToolDecision.Blocked, policy.Evaluate(Tool("create_sales_order")));
    }

    [Fact]
    public void RequireApproval_wins_over_AutoApprove()
    {
        var policy = new ToolPolicy(new ToolPolicyOptions
        {
            RequireApproval = ["search_items"],
            AutoApprove = ["search_*"],
        });

        Assert.Equal(ToolDecision.NeedsApproval, policy.Evaluate(Tool("search_items")));
        Assert.Equal(ToolDecision.Auto, policy.Evaluate(Tool("search_customers")));
    }

    [Fact]
    public void Wildcard_is_a_prefix_match_only()
    {
        var policy = new ToolPolicy(new ToolPolicyOptions { AutoApprove = ["list_*"] });

        Assert.Equal(ToolDecision.Auto, policy.Evaluate(Tool("list_open_orders")));
        Assert.Equal(ToolDecision.NeedsApproval, policy.Evaluate(Tool("unlist_everything")));
    }

    [Fact]
    public void Blocked_tools_are_hidden_from_the_model()
    {
        var policy = new ToolPolicy(new ToolPolicyOptions { Deny = ["delete_*"] });

        var visible = policy.Visible([Tool("search_items"), Tool("delete_customer")]);

        Assert.Equal(new[] { "search_items" }, visible.Select(t => t.Name));
    }

    [Fact]
    public void Warnings_flag_auto_approved_writes_and_dead_entries()
    {
        var policy = new ToolPolicy(new ToolPolicyOptions { AutoApprove = ["create_order", "no_such_tool"] });

        var warnings = policy.Warnings([Tool("create_order")]).ToList();

        Assert.Contains(warnings, w => w.Contains("create_order", StringComparison.Ordinal) && w.Contains("read-only", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("no_such_tool", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_policy_from_configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolPolicy:Deny:0"] = "create_sales_order",
                ["ToolPolicy:AutoApprove:0"] = "search_*",
                ["ToolPolicy:TrustServerAnnotations"] = "false",
            })
            .Build();

        var policy = ToolPolicy.FromConfig(config);

        Assert.False(policy.Options.TrustServerAnnotations);
        Assert.Equal(ToolDecision.Blocked, policy.Evaluate(Tool("create_sales_order")));
        Assert.Equal(ToolDecision.Auto, policy.Evaluate(Tool("search_items")));
        Assert.Equal(ToolDecision.NeedsApproval, policy.Evaluate(Tool("find_customers")));
    }
}
