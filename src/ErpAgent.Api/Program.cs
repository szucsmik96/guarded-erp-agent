using ErpAgent.Api;
using ErpAgent.Api.Data;
using ErpAgent.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration.Memory;

var builder = WebApplication.CreateBuilder(args);

// Same configuration story as the CLI: built-in defaults < appsettings < ./appsettings.Local.json < environment.
builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = Hosting.Defaults });
builder.Configuration.AddJsonFile(Path.GetFullPath("appsettings.Local.json"), optional: true);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddSingleton(sp => new Db(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not set (env: ConnectionStrings__Postgres).")));
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddSingleton(sp => ToolPolicy.FromConfig(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IChatClient>(sp => Hosting.CreateChatClient(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IToolProvider, McpToolProvider>();
builder.Services.AddHostedService<RunWorker>();
builder.Services.AddOpenApi();

var app = builder.Build();

await app.Services.GetRequiredService<Db>().MigrateAsync();

app.MapOpenApi();
app.MapAgentApi();

app.Run();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program
{
}
