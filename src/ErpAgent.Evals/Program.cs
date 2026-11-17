using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ErpAgent.Core;
using ErpAgent.Evals;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

// Usage (from the repo root):  dotnet run --project src/ErpAgent.Evals -- [--runs 3] [--only happy-path]
Console.OutputEncoding = Encoding.UTF8;

var runs = 1;
string? only = null;
var scenarioFile = Path.GetFullPath("evals/scenarios.json");
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--runs": runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--only": only = args[++i]; break;
        case "--scenarios": scenarioFile = Path.GetFullPath(args[++i]); break;
    }
}

var config = Hosting.LoadConfig();
IChatClient chat;
try
{
    chat = Hosting.CreateChatClient(config);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var scenarios = JsonSerializer.Deserialize<List<Scenario>>(
        File.ReadAllText(scenarioFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
    .Where(s => only is null || s.Id == only)
    .ToList();

var model = config["Llm:Model"]!;
Console.WriteLine($"Evaluating {scenarios.Count} scenario(s) x {runs} run(s) on {model}\n");

var results = new List<(Scenario Scenario, int Passed, List<string> Failures, TimeSpan Elapsed)>();
foreach (var scenario in scenarios)
{
    var passed = 0;
    var failures = new List<string>();
    var sw = Stopwatch.StartNew();

    for (var run = 1; run <= runs; run++)
    {
        var runFailures = await RunOnceAsync(scenario);
        if (runFailures.Count == 0)
        {
            passed++;
        }
        failures.AddRange(runFailures.Select(f => $"run {run}: {f}"));
    }

    results.Add((scenario, passed, failures, sw.Elapsed));
    Console.ForegroundColor = passed == runs ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write($"{(passed == runs ? "PASS" : "FAIL")} ");
    Console.ResetColor();
    Console.WriteLine($"{scenario.Id,-22} {passed}/{runs}");
    foreach (var f in failures)
    {
        Console.WriteLine($"       {f}");
    }

    // In GitHub Actions, also emit annotations: they show on the run page and are readable via the public API.
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
    {
        var detail = failures.Count == 0 ? "" : " | " + string.Join(" | ", failures.Take(3));
        var level = passed == runs ? "notice" : "error";
        Console.WriteLine($"::{level} title=eval {scenario.Id}::{passed}/{runs} passed{Escape(detail)}");
    }
}

var report = WriteReport(model, runs, results);
Console.WriteLine($"\nReport: {report}");
return results.All(r => r.Passed == runs) ? 0 : 1;

async Task<List<string>> RunOnceAsync(Scenario scenario)
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"erp-eval-{Guid.NewGuid():N}.db");
    try
    {
        // Always stdio with a fresh, freshly seeded database per run, so scenarios can't affect each other
        // (even if Mcp:Url points at a shared Docker server).
        await using var mcp = await Hosting.ConnectStdioAsync(config, dbPath);
        var tools = (await mcp.ListToolsAsync()).Cast<AIFunction>().ToList();

        var gate = new ScriptedApprovalGate(scenario.Approve);
        // Default policy on purpose: eval results must not depend on a local ToolPolicy override.
        var agent = new GuardedAgent(chat, tools, ToolPolicy.Default.RequiresApproval, gate, maxSteps: 12);

        var ordersBefore = CountOrders(dbPath);
        var turn = await agent.RunTurnAsync(scenario.Prompt);
        var ordersCreated = CountOrders(dbPath) - ordersBefore;

        return Grader.Grade(scenario, new RunObservation(turn, gate.Requests, ordersCreated));
    }
    catch (Exception ex)
    {
        return [$"crashed: {ex.GetType().Name}: {ex.Message}"];
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        TryDelete(dbPath);
    }
}

static string Escape(string text) =>
    text.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

static int CountOrders(string dbPath)
{
    using var c = new SqliteConnection($"Data Source={dbPath}");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Orders";
    return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
}

static void TryDelete(string path)
{
    try
    {
        File.Delete(path);
    }
    catch (IOException)
    {
        // The server process may still hold the file for a moment on Windows; temp cleanup will get it.
    }
}

static string WriteReport(
    string model, int runs, List<(Scenario Scenario, int Passed, List<string> Failures, TimeSpan Elapsed)> results)
{
    var total = results.Sum(r => r.Passed);
    var possible = results.Count * runs;
    var sb = new StringBuilder();
    sb.AppendLine(CultureInfo.InvariantCulture, $"# Eval run: {model}");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture,
        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} · {runs} run(s) per scenario · **{total}/{possible} passed**");
    sb.AppendLine();
    sb.AppendLine("| Scenario | Passed | Time | First failure |");
    sb.AppendLine("|---|---|---|---|");
    foreach (var (scenario, passed, failures, elapsed) in results)
    {
        var first = failures.FirstOrDefault()?.Replace("|", "\\|") ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| `{scenario.Id}` | {passed}/{runs} | {elapsed.TotalSeconds:0}s | {first} |");
    }

    var dir = Path.GetFullPath("evals/results");
    Directory.CreateDirectory(dir);
    var safeModel = string.Concat(model.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_'));
    var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmm}-{safeModel}.md");
    File.WriteAllText(path, sb.ToString());
    return path;
}

internal sealed class ScriptedApprovalGate(bool approve) : IApprovalGate
{
    public int Requests { get; private set; }

    public Task<ApprovalDecision> RequestAsync(string toolName, IDictionary<string, object?> arguments, CancellationToken ct)
    {
        Requests++;
        return Task.FromResult(new ApprovalDecision(approve, approve ? null : "The customer changed their mind."));
    }
}
