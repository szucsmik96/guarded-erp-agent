using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace ErpAgent.Api.Tests;

/// <summary>One real Postgres container for the whole test class.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>The real API, with the LLM and the ERP tools replaced by deterministic fakes.</summary>
public sealed class ApiFactory(string connectionString, ScriptedChatClient chat, FakeErpTools tools)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("ToolPolicy:AutoApprove:0", "read_*");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(chat);
            services.RemoveAll<IToolProvider>();
            services.AddSingleton<IToolProvider>(tools);
        });
    }
}

/// <summary>Plays back a fixed list of model responses.</summary>
public sealed class ScriptedChatClient(params ChatResponse[] script) : IChatClient
{
    private readonly Queue<ChatResponse> _script = new(script);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_script)
        {
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : Say("done"));
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse Call(string tool, string callId = "call-1") =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, new Dictionary<string, object?>())]));
}

/// <summary>A read tool and an idempotent-capable write tool that counts real executions.</summary>
public sealed class FakeErpTools : IToolProvider
{
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly List<string?> _requestIds = [];
    private int _orders;

    public FakeErpTools()
    {
        _tools =
        [
            AIFunctionFactory.Create(() => "stock: 5", "read_stock"),
            AIFunctionFactory.Create((string? requestId) =>
            {
                lock (_requestIds)
                {
                    _requestIds.Add(requestId);
                    _orders++;
                }
                return "order 42 created";
            }, "create_order"),
        ];
    }

    public int Orders => Volatile.Read(ref _orders);

    public IReadOnlyList<string?> RequestIds
    {
        get
        {
            lock (_requestIds)
            {
                return _requestIds.ToList();
            }
        }
    }

    public Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct) => Task.FromResult(_tools);
}

public static class ApiCalls
{
    public static async Task<Guid> StartRunAsync(HttpClient client, string message, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/runs") { Content = JsonContent.Create(new { message }) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var response = await client.SendAsync(request);
        await ExpectStatusAsync(response, System.Net.HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>Like Assert.Equal on the status code, but shows the response body (the exception page in Development).</summary>
    public static async Task ExpectStatusAsync(HttpResponseMessage response, System.Net.HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected {(int)expected}, got {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1200)]}");
        }
    }

    public static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid runId, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement run = default;
        while (DateTime.UtcNow < deadline)
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/runs/{runId}");
            if (run.GetProperty("status").GetString() == status)
            {
                return run;
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Run {runId} did not reach {status}. Last state: {run}");
    }

    public static async Task<JsonElement> SinglePendingApprovalAsync(HttpClient client, Guid runId)
    {
        var approvals = await client.GetFromJsonAsync<JsonElement>($"/approvals?status=pending&runId={runId}");
        return Assert.Single(approvals.EnumerateArray().ToList());
    }

    public static async Task<HttpResponseMessage> DecideAsync(
        HttpClient client, Guid approvalId, string action, string? reason = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/approvals/{approvalId}/{action}")
        {
            Content = JsonContent.Create(new { reason }),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }
}
