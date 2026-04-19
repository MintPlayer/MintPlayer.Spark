using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Boots a minimal in-memory Spark host wired against an externally-provided
/// <see cref="IDocumentStore"/> (typically supplied by <see cref="MintPlayer.Spark.Testing.SparkTestDriver"/>).
///
/// Each instance writes its model JSON files into a per-test temp content root so that
/// <c>ModelLoader</c> sees only the entity types declared by the test.
/// Uses TestServer/IHost directly rather than WebApplicationFactory&lt;T&gt; — the latter
/// requires the host assembly to expose a Main entry point, which the test project doesn't.
/// </summary>
public sealed class SparkEndpointFactory : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _contentRoot;

    public SparkEndpointFactory(IDocumentStore testStore, EntityTypeFile[] models)
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "spark-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        var modelDir = Path.Combine(_contentRoot, "App_Data", "Model");
        Directory.CreateDirectory(modelDir);

        foreach (var model in models)
        {
            var path = Path.Combine(modelDir, model.PersistentObject.Name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        }

        _host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost
                    .UseTestServer()
                    .UseContentRoot(_contentRoot)
                    .UseEnvironment("Testing")
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSpark(spark => spark.UseContext<TestSparkContext>());

                        var existing = services.Single(d => d.ServiceType == typeof(IDocumentStore));
                        services.Remove(existing);
                        services.AddSingleton(testStore);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseSpark();
                        app.UseEndpoints(endpoints => endpoints.MapSpark());
                    });
            })
            .Build();

        _host.Start();
    }

    public HttpClient CreateClient() => _host.GetTestClient();

    public T GetService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    /// <summary>
    /// Performs a warmup GET so Spark's antiforgery middleware writes both the antiforgery
    /// validation cookie (.AspNetCore.Antiforgery.*) and the readable XSRF-TOKEN cookie.
    /// Returns the raw <c>Cookie</c> header value (combining both cookies) and the
    /// X-XSRF-TOKEN request token to attach to mutating requests.
    /// TestServer's HttpClient does not auto-manage cookies, so callers must thread these
    /// through explicitly — see <see cref="SparkTestClient"/> for a wrapper that does this.
    /// </summary>
    public async Task<(string CookieHeader, string XsrfToken)> MintAntiforgeryAsync()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/spark/po/__warmup__");

        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            throw new InvalidOperationException("Warmup request did not return any Set-Cookie headers.");

        string? antiforgeryCookie = null;
        string? xsrfToken = null;

        foreach (var raw in setCookies)
        {
            var nameValue = raw.Split(';', 2)[0];
            var eq = nameValue.IndexOf('=');
            if (eq < 0) continue;
            var name = nameValue[..eq];
            var value = nameValue[(eq + 1)..];

            if (name.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
                antiforgeryCookie = nameValue;
            else if (name == "XSRF-TOKEN")
            {
                xsrfToken = Uri.UnescapeDataString(value);
            }
        }

        if (antiforgeryCookie is null || xsrfToken is null)
            throw new InvalidOperationException(
                $"Warmup did not yield both antiforgery cookies. Got: '{string.Join(" | ", setCookies)}'");

        return (antiforgeryCookie + "; XSRF-TOKEN=" + Uri.EscapeDataString(xsrfToken), xsrfToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        try
        {
            if (Directory.Exists(_contentRoot))
                Directory.Delete(_contentRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

/// <summary>
/// Minimal SparkContext for endpoint tests. Tests that need additional collections
/// can subclass or extend this via a custom fixture.
/// </summary>
public sealed class TestSparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}

public sealed class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public sealed class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
