using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ErpAgent.Core;
using ErpAgent.Evals;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

// Usage (from the repo root):
//   dotnet run --project src/ErpAgent.Evals -- [--runs 3] [--only happy-path] [--pause 5]
// --pause waits N seconds between runs, which helps with free-tier rate limits.
Console.OutputEncoding = Encoding.UTF8;

var runs = 1;
var pause = TimeSpan.Zero;
string? only = null;
var scenarioFile = Path.GetFullPath("evals/scenarios.json");
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--runs": runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--only": only = args[++i]; break;
        case "--pause": pause = TimeSpan.FromSeconds(int.Parse(args[++i], CultureInfo.InvariantCulture)); break;
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

var results = new List<ScenarioResult>();
var firstRun = true;
foreach (var scenario in scenarios)
{
    var result = new ScenarioResult(scenario);
    var sw = Stopwatch.StartNew();

    for (var run = 1; run <= runs; run++)
    {
        if (!firstRun && pause > TimeSpan.Zero)
        {
            await Task.Delay(pause);
        }
        firstRun = false;

        var (outcome, messages) = await RunOnceAsync(scenario);
        result.Record(outcome, messages.Select(m => $"run {run}: {m}"));
    }

    result.Elapsed = sw.Elapsed;
    results.Add(result);
    Print(result, runs);
}

var report = WriteReport(model, runs, results);
Console.WriteLine($"\nReport: {report}");

// 0: everything passed. 1: at least one real failure. 3: no real failures, but some runs hit provider errors.
if (results.Any(r => r.Failed > 0))
{
    return 1;
}
return results.Any(r => r.Errors > 0) ? 3 : 0;

async Task<(Outcome Outcome, List<string> Messages)> RunOnceAsync(Scenario scenario)
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

        var failures = Grader.Grade(scenario, new RunObservation(turn, gate.Requests, ordersCreated));
        return (failures.Count == 0 ? Outcome.Pass : Outcome.Fail, failures);
    }
    catch (Exception ex) when (IsProviderError(ex))
    {
        // The model provider failed (overloaded, rate-limited, timed out). That says nothing about the agent,
        // so it is reported separately and not counted as a failure.
        return (Outcome.Error, new List<string> { $"provider error: {Describe(ex)}" });
    }
    catch (Exception ex)
    {
        return (Outcome.Fail, new List<string> { $"crashed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}{ResponseBody(ex)}" });
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        TryDelete(dbPath);
    }
}

static bool IsProviderError(Exception ex) => ex switch
{
    ClientResultException { Status: 408 or 429 or >= 500 } => true,
    TaskCanceledException or TimeoutException => true,
    HttpRequestException => true,
    _ => ex.InnerException is not null && IsProviderError(ex.InnerException),
};

static string Describe(Exception ex) => ex is ClientResultException cre
    ? $"HTTP {cre.Status}{ResponseBody(ex)}"
    : $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";

// The provider's error body usually says exactly what it disliked.
static string ResponseBody(Exception ex)
{
    var body = (ex as ClientResultException)?.GetRawResponse()?.Content?.ToString();
    if (string.IsNullOrWhiteSpace(body))
    {
        return "";
    }
    body = body.ReplaceLineEndings(" ");
    return " | provider said: " + (body.Length > 600 ? body[..600] + "…" : body);
}

static void Print(ScenarioResult r, int runs)
{
    var (label, color) = r.Failed > 0 ? ("FAIL", ConsoleColor.Red)
        : r.Errors > 0 ? ("ERROR", ConsoleColor.Yellow)
        : ("PASS", ConsoleColor.Green);
    Console.ForegroundColor = color;
    Console.Write($"{label,-5} ");
    Console.ResetColor();
    Console.WriteLine($"{r.Scenario.Id,-24} {r.Passed}/{runs} passed{(r.Errors > 0 ? $", {r.Errors} provider error(s)" : "")}");
    foreach (var m in r.Messages)
    {
        Console.WriteLine($"       {m}");
    }

    // In GitHub Actions, also emit annotations: they show on the run page and are readable via the public API.
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
    {
        var level = r.Failed > 0 ? "error" : r.Errors > 0 ? "warning" : "notice";
        var detail = r.Messages.Count == 0 ? "" : " | " + string.Join(" | ", r.Messages.Take(3));
        Console.WriteLine($"::{level} title=eval {r.Scenario.Id}::{r.Passed}/{runs} passed, {r.Errors} provider errors{Escape(detail)}");
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

static string WriteReport(string model, int runs, List<ScenarioResult> results)
{
    var passed = results.Sum(r => r.Passed);
    var graded = results.Sum(r => r.Passed + r.Failed);
    var errors = results.Sum(r => r.Errors);
    var sb = new StringBuilder();
    sb.AppendLine(CultureInfo.InvariantCulture, $"# Eval run: {model}");
    sb.AppendLine();
    var errorNote = errors > 0 ? $" · {errors} run(s) not graded (provider errors)" : "";
    sb.AppendLine(CultureInfo.InvariantCulture,
        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} · {runs} run(s) per scenario · **{passed}/{graded} passed**{errorNote}");
    sb.AppendLine();
    sb.AppendLine("| Scenario | Passed | Provider errors | Time | First problem |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (var r in results)
    {
        var first = r.Messages.FirstOrDefault()?.Replace("|", "\\|") ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| `{r.Scenario.Id}` | {r.Passed}/{r.Passed + r.Failed} | {r.Errors} | {r.Elapsed.TotalSeconds:0}s | {first} |");
    }

    var dir = Path.GetFullPath("evals/results");
    Directory.CreateDirectory(dir);
    var safeModel = string.Concat(model.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_'));
    var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmm}-{safeModel}.md");
    File.WriteAllText(path, sb.ToString());
    return path;
}

internal enum Outcome { Pass, Fail, Error }

internal sealed class ScenarioResult(Scenario scenario)
{
    public Scenario Scenario { get; } = scenario;

    public int Passed { get; private set; }

    public int Failed { get; private set; }

    public int Errors { get; private set; }

    public List<string> Messages { get; } = [];

    public TimeSpan Elapsed { get; set; }

    public void Record(Outcome outcome, IEnumerable<string> messages)
    {
        switch (outcome)
        {
            case Outcome.Pass: Passed++; break;
            case Outcome.Fail: Failed++; break;
            default: Errors++; break;
        }
        Messages.AddRange(messages);
    }
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
