using System.Text.Json;

namespace ErpAgent.Core;

/// <summary>Append-only JSONL record of every tool call the agent attempts, approved or not.</summary>
public sealed class AuditLog(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    public void Write(ToolCallRecord record)
    {
        var entry = new
        {
            at = DateTimeOffset.UtcNow,
            tool = record.Tool,
            args = record.Arguments,
            requiredApproval = record.RequiredApproval,
            decision = record.Decision,
            outcome = record.Outcome is { Length: > 2000 } o ? o[..2000] + "…" : record.Outcome,
        };
        var line = JsonSerializer.Serialize(entry, Json);
        lock (_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
