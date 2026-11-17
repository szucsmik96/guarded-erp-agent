using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;

namespace ErpAgent.Api.Data;

/// <summary>
/// Idempotency-Key support for POST endpoints. The first request with a key runs and its response is stored;
/// a retry with the same key and the same request gets the stored response back without running again.
/// The same key with a different request is rejected (422); a retry while the first is still running gets 409.
/// </summary>
public sealed class IdempotencyStore(Db db)
{
    public const string Header = "Idempotency-Key";

    public async Task<IResult> ExecuteAsync(
        HttpRequest http, object request, Func<Task<(int Status, object Body)>> action, CancellationToken ct)
    {
        var key = http.Headers[Header].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            var (status, body) = await action();
            return Results.Json(body, ApiJson.Options, statusCode: status);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{http.Method} {http.Path}\n{JsonSerializer.Serialize(request, ApiJson.Options)}")));

        await using var c = await db.OpenAsync(ct);
        var inserted = await c.ExecuteAsync(
            "INSERT INTO idempotency_keys (key, fingerprint) VALUES (@key, @fingerprint) ON CONFLICT (key) DO NOTHING",
            new { key, fingerprint });

        if (inserted == 0)
        {
            var stored = await c.QuerySingleAsync<StoredResponse>(
                "SELECT fingerprint AS Fingerprint, status AS Status, body::text AS Body FROM idempotency_keys WHERE key = @key",
                new { key });
            if (stored.Fingerprint != fingerprint)
            {
                return Results.Json(new { error = $"{Header} was already used with a different request." }, ApiJson.Options, statusCode: 422);
            }
            if (stored.Status is null)
            {
                return Results.Json(new { error = $"A request with this {Header} is still in progress." }, ApiJson.Options, statusCode: 409);
            }
            return Results.Text(stored.Body ?? "null", "application/json", Encoding.UTF8, stored.Status.Value);
        }

        try
        {
            var (status, body) = await action();
            var json = JsonSerializer.Serialize(body, ApiJson.Options);
            await c.ExecuteAsync(
                "UPDATE idempotency_keys SET status = @status, body = @json::jsonb WHERE key = @key",
                new { status, json, key });
            return Results.Text(json, "application/json", Encoding.UTF8, status);
        }
        catch
        {
            // Free the key so the client can retry after a failure.
            await c.ExecuteAsync("DELETE FROM idempotency_keys WHERE key = @key", new { key });
            throw;
        }
    }

    private sealed class StoredResponse
    {
        public string Fingerprint { get; set; } = "";

        public int? Status { get; set; }

        public string? Body { get; set; }
    }
}
