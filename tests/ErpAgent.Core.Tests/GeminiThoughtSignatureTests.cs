using System.Text.Json.Nodes;

namespace ErpAgent.Core.Tests;

public sealed class GeminiThoughtSignatureTests
{
    private const string Response = """
        {"choices":[{"message":{"role":"assistant","tool_calls":[
          {"id":"call_a","type":"function","function":{"name":"find_customers","arguments":"{}"},
           "extra_content":{"google":{"thought_signature":"SIG-A"}}},
          {"id":"call_b","type":"function","function":{"name":"search_items","arguments":"{}"}}
        ]}}]}
        """;

    private static string Request(params string[] callIds) => new JsonObject
    {
        ["model"] = "gemini",
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "hi" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray(callIds.Select(id => (JsonNode?)new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "x", ["arguments"] = "{}" },
                }).ToArray()),
            }),
    }.ToJsonString();

    private static string? SignatureOf(string json, int call) =>
        JsonNode.Parse(json)!["messages"]![1]!["tool_calls"]![call]!["extra_content"]?["google"]?["thought_signature"]?.GetValue<string>();

    [Fact]
    public void Captured_signature_is_put_back_on_the_matching_call()
    {
        var policy = new GeminiThoughtSignaturePolicy();
        policy.Capture(Response);

        var rewritten = policy.Rewrite(Request("call_a", "call_b"));

        Assert.NotNull(rewritten);
        Assert.Equal("SIG-A", SignatureOf(rewritten, 0));
        Assert.Null(SignatureOf(rewritten, 1));
    }

    [Fact]
    public void Unknown_signature_falls_back_to_the_skip_value_on_the_first_call_only()
    {
        var rewritten = new GeminiThoughtSignaturePolicy().Rewrite(Request("call_x", "call_y"));

        Assert.Equal(GeminiThoughtSignaturePolicy.SkipValidator, SignatureOf(rewritten!, 0));
        Assert.Null(SignatureOf(rewritten!, 1));
    }

    [Fact]
    public void Requests_without_tool_calls_are_left_alone()
    {
        var body = """{"model":"gemini","messages":[{"role":"user","content":"hi"}]}""";

        Assert.Null(new GeminiThoughtSignaturePolicy().Rewrite(body));
    }

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://localhost:11434/v1", false)]
    public void Only_google_endpoints_get_the_policy(string endpoint, bool expected) =>
        Assert.Equal(expected, GeminiThoughtSignaturePolicy.AppliesTo(new Uri(endpoint)));
}
