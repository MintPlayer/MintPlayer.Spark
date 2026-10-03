using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// A POST the <c>SparkClient</c> refuses to send — an update or delete without an etag (#467, D14/D16)
/// — so a test can show the server refuses it too.
/// </summary>
internal static class RawSparkPost
{
    public static async Task<(HttpStatusCode Status, string Body)> SendAsync<TContext>(
        SparkEndpointFactory<TContext> factory, string url, object payload) where TContext : SparkContext
    {
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary><c>POST /spark/po/update</c> of <paramref name="po"/> exactly as it is, etag or not.</summary>
    public static Task<(HttpStatusCode Status, string Body)> UpdateAsync<TContext>(
        SparkEndpointFactory<TContext> factory, Abstractions.PersistentObject po) where TContext : SparkContext
        => SendAsync(factory, "/spark/po/update", new { objectTypeId = po.ObjectTypeId.ToString(), id = po.Id, persistentObject = po });
}
