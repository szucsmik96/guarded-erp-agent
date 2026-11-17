using System.Text;
using ErpAgent.Core;
using Microsoft.Extensions.AI;

Console.OutputEncoding = Encoding.UTF8;

var config = Hosting.LoadConfig();

IChatClient chat;
try
{
    chat = Hosting.CreateChatClient(config);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

await using var mcp = await Hosting.ConnectAsync(config, stderr: line => Console.Error.WriteLine($"   [server] {line}"));

var allTools = (await mcp.ListToolsAsync()).Cast<AIFunction>().ToList();
var policy = ToolPolicy.FromConfig(config);
Console.WriteLine($"Connected to the ERP MCP server ({config["Mcp:Url"] ?? "local, stdio"}). {allTools.Count} tools:");
foreach (var tool in allTools)
{
    var label = policy.Evaluate(tool) switch
    {
        ToolDecision.Auto => "[auto]             ",
        ToolDecision.NeedsApproval => "[needs approval]   ",
        _ => "[blocked by policy]",
    };
    Console.WriteLine($"  {label} {tool.Name}");
}
foreach (var warning in policy.Warnings(allTools))
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  policy warning: {warning}");
    Console.ForegroundColor = previous;
}
var tools = policy.Visible(allTools);

var agent = new GuardedAgent(
    chat,
    tools,
    policy.RequiresApproval,
    new ConsoleApprovalGate(),
    new AuditLog(Path.GetFullPath(config["Agent:AuditFile"]!)),
    maxSteps: int.TryParse(config["Agent:MaxSteps"], out var steps) ? steps : 12,
    trace: Trace);

Console.WriteLine("\nAsk for something, e.g. \"Order 4 standing desks for Nordic Furnishings\". Type exit to quit.");
while (true)
{
    Console.Write("\nyou> ");
    var input = Console.ReadLine();
    if (input is null || input.Trim() is "exit" or "quit")
    {
        break;
    }
    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    try
    {
        var turn = await agent.RunTurnAsync(input);
        Console.WriteLine($"\nagent> {turn.Answer}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
    }
}

return 0;

static void Trace(string message)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"   {message}");
    Console.ForegroundColor = previous;
}
