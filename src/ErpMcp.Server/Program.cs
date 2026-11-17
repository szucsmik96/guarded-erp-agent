using System.Security.Cryptography;
using System.Text;
using ErpMcp.Server.Data;

// Modes:
//   (default)            stdio: launched as a child process by the agent, Claude Desktop or Claude Code
//   MCP_TRANSPORT=http   Streamable HTTP on /mcp, for Docker / remote use; requires MCP_API_KEY
//   healthcheck          used by the container HEALTHCHECK (the aspnet image has no curl)

if (args is ["healthcheck", ..])
{
    return await HealthcheckAsync();
}

var transport = (Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio").Trim().ToLowerInvariant();
return transport switch
{
    "stdio" => await RunStdioAsync(args),
    "http" => await RunHttpAsync(args),
    _ => Fail($"Unknown MCP_TRANSPORT '{transport}'. Use 'stdio' or 'http'."),
};

static async Task<int> RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdout carries the MCP protocol, so every log line must go to stderr.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    AddErp(builder.Services, builder.Configuration);
    builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

    var app = builder.Build();
    app.Services.GetRequiredService<ErpDb>().EnsureCreatedAndSeeded();
    await app.RunAsync();
    return 0;
}

static async Task<int> RunHttpAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    AddErp(builder.Services, builder.Configuration);
    builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

    var app = builder.Build();

    // Fail closed: a network-reachable ERP endpoint without auth is a mistake unless explicitly intended.
    var apiKey = app.Configuration["MCP_API_KEY"];
    var allowAnonymous = string.Equals(app.Configuration["MCP_ALLOW_ANONYMOUS"], "true", StringComparison.OrdinalIgnoreCase);
    if (string.IsNullOrEmpty(apiKey) && !allowAnonymous)
    {
        app.Logger.LogCritical("MCP_API_KEY is not set. Set it, or MCP_ALLOW_ANONYMOUS=true for local experiments.");
        return 1;
    }

    if (!string.IsNullOrEmpty(apiKey))
    {
        var expected = Encoding.UTF8.GetBytes($"Bearer {apiKey}");
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp"))
            {
                var actual = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next(ctx);
        });
    }

    app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
    app.MapMcp("/mcp");

    app.Services.GetRequiredService<ErpDb>().EnsureCreatedAndSeeded();
    await app.RunAsync();
    return 0;
}

static void AddErp(IServiceCollection services, IConfiguration config)
{
    var dbPath = config["ERP_DB_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "erp.db");
    services.AddSingleton(new ErpDb($"Data Source={dbPath}"));
    services.AddSingleton<OrderService>();
}

static async Task<int> HealthcheckAsync()
{
    var url = Environment.GetEnvironmentVariable("HEALTHCHECK_URL") ?? "http://localhost:8080/healthz";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
