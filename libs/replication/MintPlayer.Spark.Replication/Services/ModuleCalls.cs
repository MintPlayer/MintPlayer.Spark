using System.Net;
using System.Net.Http.Json;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>One call to another module and how it ended.</summary>
/// <param name="StatusCode">The HTTP status, or null when no answer arrived (unreachable, timed out).</param>
/// <param name="Body">The answer's body text, for logs and refusals.</param>
internal sealed record ModuleCallResult<T>(HttpStatusCode? StatusCode, T? Value, string? Body, Exception? Error = null)
{
    public bool IsSuccess => StatusCode is { } code && (int)code is >= 200 and < 300;

    /// <summary>
    /// Worth retrying: no answer, a 5xx, a 408 or a 429. Any other 4xx is the other module's settled answer.
    /// </summary>
    public bool IsTransient => StatusCode is not { } code || (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

/// <summary>Module-to-module JSON calls over the module's client certificate (the mTLS channel, §4.2.6).</summary>
internal static class ModuleCalls
{
    public static async Task<ModuleCallResult<TResult>> PostAsync<TBody, TResult>(
        HttpClient client, string baseUrl, string path, TBody body, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}{path}";
        try
        {
            using var response = await client.PostAsJsonAsync(url, body, cancellationToken);
            return await ReadAsync<TResult>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ModuleCallResult<TResult>(null, default, null, ex);
        }
    }

    public static async Task<ModuleCallResult<TResult>> GetAsync<TResult>(HttpClient client, string baseUrl, string pathAndQuery, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}{pathAndQuery}";
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            return await ReadAsync<TResult>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ModuleCallResult<TResult>(null, default, null, ex);
        }
    }

    private static async Task<ModuleCallResult<TResult>> ReadAsync<TResult>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        TResult? value = default;
        if (text.Length > 0)
        {
            try { value = System.Text.Json.JsonSerializer.Deserialize<TResult>(text, System.Text.Json.JsonSerializerOptions.Web); }
            catch (System.Text.Json.JsonException) { }
        }
        return new ModuleCallResult<TResult>(response.StatusCode, value, text);
    }
}
