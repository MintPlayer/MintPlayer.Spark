using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-H4 — LookupReference mutation endpoints (POST /spark/lookupref/{name},
/// PUT/DELETE /spark/lookupref/{name}/{key}) used to accept any caller with a valid
/// XSRF token. They're now gated behind Edit/LookupReferences. Fleet's
/// security.json doesn't grant that to Everyone or Fleet managers — only admins.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class LookupReferenceAuthTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public LookupReferenceAuthTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private HttpClient CreateClientWithXsrf(out string xsrfToken)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri(_fixture.Host.FleetUrl) };

        // Warm up to get the XSRF cookie
        var warmup = http.GetAsync("/spark/types").GetAwaiter().GetResult();
        warmup.EnsureSuccessStatusCode();

        var cookies = handler.CookieContainer.GetCookies(new Uri(_fixture.Host.FleetUrl));
        xsrfToken = cookies["XSRF-TOKEN"]?.Value
            ?? throw new InvalidOperationException("No XSRF-TOKEN cookie issued");
        // The cookie value is URL-encoded
        xsrfToken = Uri.UnescapeDataString(xsrfToken);

        return http;
    }

    [Fact]
    public async Task Anonymous_post_lookupref_value_is_refused()
    {
        using var http = CreateClientWithXsrf(out var xsrfToken);

        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/lookupref/CarStatus")
        {
            Content = JsonContent.Create(new { Key = "test-key", Value = "test-value", DisplayName = "Test" })
        };
        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);

        var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "anonymous caller must not be able to write LookupReferences — was previously 200/201");
    }

    [Fact]
    public async Task Anonymous_delete_lookupref_value_is_refused()
    {
        using var http = CreateClientWithXsrf(out var xsrfToken);

        var request = new HttpRequestMessage(HttpMethod.Delete, "/spark/lookupref/CarStatus/some-key");
        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);

        var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "anonymous caller must not be able to delete LookupReferences");
    }
}
