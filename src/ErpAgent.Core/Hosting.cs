using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using OpenAI;

namespace ErpAgent.Core;

/// <summary>Shared setup for the CLI and the eval runner. Run both from the repo root.</summary>
public static class Hosting
{
    public const string DefaultServerProject = "src/ErpMcp.Server";

    /// <summary>Built-in defaults; every other configuration source overrides them.</summary>
    public static readonly IReadOnlyDictionary<string, string?> Defaults = new Dictionary<string, string?>
    {
        ["Llm:Endpoint"] = "https://api.openai.com/v1",
        ["Llm:Model"] = "gpt-5-mini",
        ["Llm:MaxRetries"] = "6",
        ["Llm:TimeoutSeconds"] = "180",
        ["Mcp:ServerProject"] = DefaultServerProject,
        ["Agent:MaxSteps"] = "12",
        ["Agent:AuditFile"] = "agent-audit.jsonl",
    };

    /// <summary>Defaults, then ./appsettings.Local.json (gitignored), then environment variables (Llm__ApiKey, ...).</summary>
    public static IConfiguration LoadConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(Defaults)
            .AddJsonFile(Path.GetFullPath("appsettings.Local.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>Any OpenAI-compatible endpoint: OpenAI, Azure OpenAI, or Ollama / vLLM on-prem.</summary>
    public static IChatClient CreateChatClient(IConfiguration config)
    {
        var apiKey = config["Llm:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Set Llm:ApiKey in appsettings.Local.json (repo root) or the Llm__ApiKey environment variable. " +
                "For a local Ollama endpoint any non-empty value works.");
        }

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(config["Llm:Endpoint"]!),

            // Retries 429 and 5xx with exponential backoff, honouring Retry-After. Free tiers need this.
            RetryPolicy = new ClientRetryPolicy(maxRetries: int.Parse(config["Llm:MaxRetries"]!, CultureInfo.InvariantCulture)),
            NetworkTimeout = TimeSpan.FromSeconds(int.Parse(config["Llm:TimeoutSeconds"]!, CultureInfo.InvariantCulture)),
        };

        if (GeminiThoughtSignaturePolicy.AppliesTo(options.Endpoint))
        {
            options.AddPolicy(new GeminiThoughtSignaturePolicy(), PipelinePosition.PerCall);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(config["Llm:Model"]!)
            .AsIChatClient();
    }

    /// <summary>
    /// Mcp:Url set → connect to a running server over Streamable HTTP (e.g. the Docker container),
    /// sending Mcp:ApiKey as a bearer token. Otherwise start the server locally over stdio.
    /// </summary>
    public static Task<McpClient> ConnectAsync(IConfiguration config, Action<string>? stderr = null, CancellationToken ct = default)
    {
        var url = config["Mcp:Url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            return ConnectStdioAsync(config, dbPath: null, stderr, ct);
        }

        var headers = new Dictionary<string, string>();
        if (config["Mcp:ApiKey"] is { Length: > 0 } key)
        {
            headers["Authorization"] = $"Bearer {key}";
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "erp",
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
        });

        return McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    /// <summary>
    /// How to start the server over stdio, in order of preference:
    /// Mcp:ServerCommand, then a server executable next to this one (the self-contained bundle),
    /// then `dotnet run` on the source project (development).
    /// </summary>
    public static (string Command, string[] Arguments) ResolveServerCommand(IConfiguration config)
    {
        if (config["Mcp:ServerCommand"] is { Length: > 0 } command)
        {
            return (command, Array.Empty<string>());
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ErpMcp.Server.exe" : "ErpMcp.Server");
        if (File.Exists(bundled))
        {
            return (bundled, Array.Empty<string>());
        }

        return ("dotnet", ["run", "--project", Path.GetFullPath(config["Mcp:ServerProject"]!), "--no-build"]);
    }

    /// <summary>Starts the ERP MCP server as a child process and connects over stdio.</summary>
    public static Task<McpClient> ConnectStdioAsync(
        IConfiguration config, string? dbPath = null, Action<string>? stderr = null, CancellationToken ct = default)
    {
        var env = new Dictionary<string, string?>
        {
            ["MCP_TRANSPORT"] = "stdio",
            ["Logging__LogLevel__Default"] = "Warning",
        };
        if (dbPath is not null)
        {
            env["ERP_DB_PATH"] = dbPath;
        }

        var (command, arguments) = ResolveServerCommand(config);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "erp",
            Command = command,
            Arguments = arguments,
            EnvironmentVariables = env,
            StandardErrorLines = stderr,
        });

        return McpClient.CreateAsync(transport, cancellationToken: ct);
    }
}
