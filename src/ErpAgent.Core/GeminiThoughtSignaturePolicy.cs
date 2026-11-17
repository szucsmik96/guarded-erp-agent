using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ErpAgent.Core;

/// <summary>
/// Gemini's thinking models attach a "thought signature" to each tool call and require it back on the next
/// request. In the OpenAI-compatible API it lives in a non-standard field the OpenAI SDK drops:
/// <c>tool_calls[].extra_content.google.thought_signature</c>. This pipeline policy captures signatures from
/// responses and re-attaches them to the matching tool calls in later requests. If a signature is unknown
/// (e.g. after a restart), the first call of that message gets Google's documented skip value instead.
/// Only installed for Google endpoints; other providers never see the extra field.
/// </summary>
public sealed class GeminiThoughtSignaturePolicy : PipelinePolicy
{
    public const string SkipValidator = "skip_thought_signature_validator";

    private const int MaxRemembered = 10_000;
    private readonly ConcurrentDictionary<string, string> _signatures = new();

    public static bool AppliesTo(Uri endpoint) =>
        endpoint.Host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase);

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Before(message);
        ProcessNext(message, pipeline, currentIndex);
        After(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Before(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        After(message);
    }

    /// <summary>Remembers every thought signature found in a chat-completions response body.</summary>
    public void Capture(string responseJson)
    {
        if (JsonNode.Parse(responseJson) is not JsonObject root || root["choices"] is not JsonArray choices)
        {
            return;
        }

        foreach (var toolCall in choices.OfType<JsonObject>()
                     .Select(c => c["message"]?["tool_calls"]).OfType<JsonArray>()
                     .SelectMany(calls => calls.OfType<JsonObject>()))
        {
            var id = toolCall["id"]?.GetValue<string>();
            var signature = toolCall["extra_content"]?["google"]?["thought_signature"]?.GetValue<string>();
            if (id is null || signature is null)
            {
                continue;
            }

            if (_signatures.Count >= MaxRemembered)
            {
                _signatures.Clear();
            }
            _signatures[id] = signature;
        }
    }

    /// <summary>Returns the request body with signatures re-attached, or null if nothing needed changing.</summary>
    public string? Rewrite(string requestJson)
    {
        if (JsonNode.Parse(requestJson) is not JsonObject root || root["messages"] is not JsonArray messages)
        {
            return null;
        }

        var changed = false;
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["role"]?.GetValue<string>() != "assistant" || message["tool_calls"] is not JsonArray toolCalls)
            {
                continue;
            }

            var calls = toolCalls.OfType<JsonObject>().ToList();
            var anySigned = false;
            foreach (var call in calls)
            {
                if (call["extra_content"] is not null)
                {
                    anySigned = true;
                    continue;
                }
                if (call["id"]?.GetValue<string>() is { } id && _signatures.TryGetValue(id, out var signature))
                {
                    call["extra_content"] = Signature(signature);
                    anySigned = true;
                    changed = true;
                }
            }

            // Gemini validates the first function call of each step; without the original, use the skip value.
            if (!anySigned && calls.Count > 0)
            {
                calls[0]["extra_content"] = Signature(SkipValidator);
                changed = true;
            }
        }

        return changed ? root.ToJsonString() : null;
    }

    private static JsonObject Signature(string value) =>
        new() { ["google"] = new JsonObject { ["thought_signature"] = value } };

    private void Before(PipelineMessage message)
    {
        if (message.Request.Content is not { } content)
        {
            return;
        }

        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        var rewritten = Rewrite(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        if (rewritten is not null)
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
    }

    private void After(PipelineMessage message)
    {
        try
        {
            if (message.Response is { IsError: false } response)
            {
                Capture(response.Content.ToString());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            // Unbuffered or non-JSON response: nothing to capture.
        }
    }
}
