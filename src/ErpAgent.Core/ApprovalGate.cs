using System.Text.Json;

namespace ErpAgent.Core;

public sealed record ApprovalDecision(bool Approved, string? Reason = null);

/// <summary>Human-in-the-loop checkpoint. The console version is the first; a web UI or Teams card can implement the same interface.</summary>
public interface IApprovalGate
{
    Task<ApprovalDecision> RequestAsync(string toolName, IDictionary<string, object?> arguments, CancellationToken ct);
}

public sealed class ConsoleApprovalGate : IApprovalGate
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public Task<ApprovalDecision> RequestAsync(string toolName, IDictionary<string, object?> arguments, CancellationToken ct)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine($"!! The agent wants to run a WRITE action: {toolName}");
        Console.ForegroundColor = previous;
        Console.WriteLine(JsonSerializer.Serialize(arguments, Pretty));
        Console.Write("Approve? [y/N] ");

        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is "y" or "yes")
        {
            return Task.FromResult(new ApprovalDecision(true));
        }

        Console.Write("Reason (optional, passed back to the agent): ");
        var reason = Console.ReadLine();
        return Task.FromResult(new ApprovalDecision(false, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()));
    }
}
