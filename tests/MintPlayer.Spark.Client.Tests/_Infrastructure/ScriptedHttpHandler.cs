using System.Net;
using System.Text.Json;

namespace MintPlayer.Spark.Client.Tests._Infrastructure;

/// <summary>
/// Test HttpMessageHandler that returns responses from a queue and records every request it
/// sees. Lets tests assert on exact URL, method, headers, and ordering without standing up a
/// whole TestServer — ideal for SparkClient edge-case tests (warmup failure shapes, query
/// parameter encoding, etc.).
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public ScriptedHttpHandler Enqueue(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public ScriptedHttpHandler EnqueueOk() => Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

    public ScriptedHttpHandler EnqueueStatus(HttpStatusCode status) => Enqueue(new HttpResponseMessage(status));

    /// <summary>Enqueue a 200 response that drops exactly the supplied Set-Cookie headers.</summary>
    public ScriptedHttpHandler EnqueueWithCookies(params string[] setCookies)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var sc in setCookies)
            response.Headers.TryAddWithoutValidation("Set-Cookie", sc);
        return Enqueue(response);
    }

    /// <summary>
    /// The JSON body of each request, by the same index as <see cref="Requests"/>; <c>null</c> where
    /// the request carried none.
    /// </summary>
    /// <remarks>
    /// ⚠️ Captured here rather than left on the cloned request, because the content stream is drained
    /// once <c>SendAsync</c> returns — reading it from a test gives a <c>NullReferenceException</c>,
    /// not an empty string. It became worth capturing when the endpoints moved their parameters out
    /// of the URL: the body <i>is</i> the request now, and a handler that records only the URL would
    /// let a test assert on the half that no longer carries anything.
    /// </remarks>
    public List<string?> Bodies { get; } = new();

    /// <summary>The parsed body of request <paramref name="index"/>.</summary>
    public JsonElement Body(int index)
        => JsonDocument.Parse(Bodies[index] ?? throw new InvalidOperationException(
            $"Request {index} ({Requests[index].Method} {Requests[index].RequestUri}) carried no body."))
            .RootElement.Clone();

    /// <summary>The parsed body of the last request — the one under test after a warmup.</summary>
    public JsonElement LastBody() => Body(Bodies.Count - 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(CloneRequestForInspection(request));
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No queued response for {request.Method} {request.RequestUri}.");
        return _responses.Dequeue();
    }

    // HttpRequestMessage is consumed after SendAsync returns (content stream drained);
    // snapshot the bits tests care about so they remain inspectable.
    private static HttpRequestMessage CloneRequestForInspection(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
