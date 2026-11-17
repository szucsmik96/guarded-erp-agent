using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;

namespace ErpAgent.Core;

public enum ToolDecision
{
    /// <summary>Runs without asking a human.</summary>
    Auto,

    /// <summary>Waits for human approval.</summary>
    NeedsApproval,

    /// <summary>Hidden from the model and never executed.</summary>
    Blocked,
}

/// <summary>Local, operator-owned rules. Entries are exact tool names or prefixes ending in '*', e.g. "search_*".</summary>
public sealed record ToolPolicyOptions
{
    public IReadOnlyList<string> Deny { get; init; } = [];

    public IReadOnlyList<string> RequireApproval { get; init; } = [];

    public IReadOnlyList<string> AutoApprove { get; init; } = [];

    /// <summary>
    /// When true (default), a tool the server annotates readOnlyHint=true runs without approval.
    /// Set false for servers you don't control: then only AutoApprove entries skip approval.
    /// </summary>
    public bool TrustServerAnnotations { get; init; } = true;
}

/// <summary>
/// Decides, per tool, whether it runs automatically, needs approval, or is blocked.
/// Precedence: Deny > RequireApproval > AutoApprove > server read-only annotation (if trusted) > approval.
/// Anything not explicitly allowed needs a human: the policy fails closed.
/// </summary>
public sealed class ToolPolicy(ToolPolicyOptions options)
{
    public static ToolPolicy Default { get; } = new(new ToolPolicyOptions());

    public ToolPolicyOptions Options { get; } = options;

    public ToolDecision Evaluate(AIFunction tool)
    {
        var name = tool.Name;
        if (Matches(Options.Deny, name))
        {
            return ToolDecision.Blocked;
        }
        if (Matches(Options.RequireApproval, name))
        {
            return ToolDecision.NeedsApproval;
        }
        if (Matches(Options.AutoApprove, name))
        {
            return ToolDecision.Auto;
        }
        if (Options.TrustServerAnnotations && IsAnnotatedReadOnly(tool))
        {
            return ToolDecision.Auto;
        }
        return ToolDecision.NeedsApproval;
    }

    public bool RequiresApproval(AIFunction tool) => Evaluate(tool) != ToolDecision.Auto;

    /// <summary>The tools the model is allowed to see: everything not blocked.</summary>
    public List<AIFunction> Visible(IEnumerable<AIFunction> tools) =>
        tools.Where(t => Evaluate(t) != ToolDecision.Blocked).ToList();

    /// <summary>Configuration smells worth showing the operator at startup.</summary>
    public IEnumerable<string> Warnings(IReadOnlyList<AIFunction> tools)
    {
        foreach (var tool in tools)
        {
            if (Evaluate(tool) == ToolDecision.Auto && !IsAnnotatedReadOnly(tool))
            {
                yield return $"'{tool.Name}' is auto-approved by local policy, but the server does not mark it read-only.";
            }
        }

        var names = tools.Select(t => t.Name).ToList();
        foreach (var entry in Options.Deny.Concat(Options.RequireApproval).Concat(Options.AutoApprove))
        {
            if (!names.Any(n => Matches([entry], n)))
            {
                yield return $"Policy entry '{entry}' matches no tool on this server.";
            }
        }
    }

    /// <summary>Reads the "ToolPolicy" configuration section. Missing section = default policy.</summary>
    public static ToolPolicy FromConfig(IConfiguration config)
    {
        var section = config.GetSection("ToolPolicy");
        return new ToolPolicy(new ToolPolicyOptions
        {
            Deny = List(section, "Deny"),
            RequireApproval = List(section, "RequireApproval"),
            AutoApprove = List(section, "AutoApprove"),
            TrustServerAnnotations = !string.Equals(section["TrustServerAnnotations"], "false", StringComparison.OrdinalIgnoreCase),
        });
    }

    public static bool IsAnnotatedReadOnly(AIFunction tool) =>
        tool is McpClientTool mcp && mcp.ProtocolTool.Annotations?.ReadOnlyHint == true;

    private static bool Matches(IReadOnlyList<string> patterns, string name)
    {
        foreach (var pattern in patterns)
        {
            var matched = pattern.EndsWith('*')
                ? name.StartsWith(pattern[..^1], StringComparison.Ordinal)
                : string.Equals(name, pattern, StringComparison.Ordinal);
            if (matched)
            {
                return true;
            }
        }
        return false;
    }

    private static List<string> List(IConfigurationSection section, string key) =>
        section.GetSection(key).GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();
}
