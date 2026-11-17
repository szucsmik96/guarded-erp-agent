using System.Text.Json;
using System.Text.Json.Serialization;
using ErpAgent.Core;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ErpAgent.Api;

/// <summary>Where the agent's tools come from. Production: the ERP MCP server. Tests: in-process fakes.</summary>
public interface IToolProvider
{
    Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct);
}

/// <summary>Connects to the MCP server once (stdio or HTTP, per configuration) and reuses the connection.</summary>
public sealed class McpToolProvider(IConfiguration config, ILogger<McpToolProvider> log) : IToolProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<AIFunction>? _tools;

    public async Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct)
    {
        if (_tools is not null)
        {
            return _tools;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_tools is null)
            {
                _client = await Hosting.ConnectAsync(config, stderr: line => log.LogInformation("[erp-mcp] {Line}", line), ct: ct);
                _tools = (await _client.ListToolsAsync(cancellationToken: ct)).Cast<AIFunction>().ToList();
                log.LogInformation("Connected to the ERP MCP server with {Count} tools", _tools.Count);
            }
            return _tools;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
        _lock.Dispose();
    }
}

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}
