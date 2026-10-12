using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeoTester.Tests._Infrastructure;

/// <summary>
/// An AI agent against SeoTester's real <c>/mcp</c> (<c>docs/spark_mcp_PRD.md</c> §4.7): raw JSON-RPC over the Streamable HTTP
/// transport, so status codes and headers stay observable, and personal access tokens created through the account page's
/// own endpoint (the user's grant, D11/D12).
/// </summary>
internal static class SeoMcp
{
    private static int nextId;

    public sealed record Reply(HttpStatusCode Status, JsonNode? Body, string Raw, HttpResponseMessage Response)
    {
        public JsonNode? Result => Body?["result"];
        public string? ErrorMessage => Body?["error"]?["message"]?.GetValue<string>();
        public bool IsToolError => Result?["isError"]?.GetValue<bool>() == true;
        public string ToolText => Result?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
        public IReadOnlyList<string> ToolNames => Result?["tools"]?.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList() ?? [];

        /// <summary>The JSON between the answer's data fence, parsed (the last fence: the preamble names the tag first).</summary>
        public JsonNode Data
        {
            get
            {
                var tag = FenceTag ?? throw new InvalidOperationException($"No data fence in: {ToolText}");
                var start = ToolText.LastIndexOf($"<{tag}>", StringComparison.Ordinal) + tag.Length + 2;
                var end = ToolText.LastIndexOf($"</{tag}>", StringComparison.Ordinal);
                return JsonNode.Parse(ToolText[start..end])!;
            }
        }

        /// <summary>The per-call fence tag (<c>data-…</c>) the answer's preamble names, or null.</summary>
        public string? FenceTag
            => System.Text.RegularExpressions.Regex.Match(ToolText, "Text between <(data-[0-9a-f]+)>") is { Success: true } m ? m.Groups[1].Value : null;

        /// <summary>The answer's text outside the data fence: what the application itself says.</summary>
        public string Note
        {
            get
            {
                if (FenceTag is not { } tag)
                    return ToolText;
                var start = ToolText.LastIndexOf($"<{tag}>", StringComparison.Ordinal);
                var end = ToolText.LastIndexOf($"</{tag}>", StringComparison.Ordinal);
                return ToolText[..start] + ToolText[(end + tag.Length + 3)..];
            }
        }
    }

    public static Task<Reply> ListToolsAsync(HttpClient client, string? token)
        => SendAsync(client, "tools/list", new { }, token);

    public static Task<Reply> CallAsync(HttpClient client, string tool, object arguments, string? token)
        => SendAsync(client, "tools/call", new { name = tool, arguments }, token);

    public static async Task<Reply> SendAsync(HttpClient client, string method, object @params, string? token)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref nextId), method, @params });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Reply(response.StatusCode, Parse(text), text, response);
    }

    /// <summary>
    /// A personal access token of <paramref name="user"/> (D11), made on the account page's endpoint as the user would:
    /// <paramref name="permissions"/> <c>Read</c> or <c>Read, Write</c> ("changes"), every exposed type.
    /// </summary>
    public static async Task<string> TokenAsync(SeoTesterHostFixture host, string user, string permissions = "Read")
    {
        using var client = host.CreateClient();
        var (status, body, raw) = await SeoRequests.PostAsync(client, "/spark/auth/manage/agents/tokens",
            new { name = "tests " + Guid.NewGuid().ToString("N")[..8], permissions, expiresInDays = 7 }, user);
        status.Should().Be(HttpStatusCode.OK, raw);
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>A JSON body, or the last <c>data:</c> event of an SSE body.</summary>
    private static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
            return JsonNode.Parse(trimmed);

        var data = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("data:", StringComparison.Ordinal)).LastOrDefault();
        return data is null ? null : JsonNode.Parse(data["data:".Length..].Trim());
    }
}
