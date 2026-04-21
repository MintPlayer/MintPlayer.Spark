using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Boots a minimal in-memory Spark host wired against an externally-supplied
/// <see cref="IDocumentStore"/> (typically obtained from <see cref="SparkTestDriver"/>).
/// Each instance writes its model JSON files into a per-test temp content root so
/// <c>ModelLoader</c> sees only the entity types declared by the fixture.
///
/// Uses <see cref="TestServer"/>/<see cref="IHost"/> directly rather than
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> —
/// the latter requires the host assembly to expose a <c>Main</c> entry point, which a
/// library-level test project doesn't have.
///
/// <typeparamref name="TContext"/> is the <see cref="SparkContext"/> subclass your tests
/// expose to Spark (e.g. a context with <c>IRavenQueryable&lt;Person&gt; People</c>).
/// Library code lives here so downstream test projects only write a thin subclass, or
/// construct <c>SparkEndpointFactory&lt;TMyContext&gt;</c> directly with no boilerplate.
/// </summary>
public class SparkEndpointFactory<TContext> : IAsyncDisposable
    where TContext : SparkContext
{
    private readonly IHost _host;
    private readonly string _contentRoot;

    /// <param name="testStore">The in-memory (or otherwise) document store the host should use.</param>
    /// <param name="models">Entity type definitions; serialized into <c>App_Data/Model/*.json</c>.</param>
    /// <param name="configureServices">
    /// Optional hook invoked after the built-in registrations (AddRouting, AddSpark, testStore override).
    /// Use it to register custom actions classes, options, or swap services for mocks without forking
    /// the factory.
    /// </param>
    public SparkEndpointFactory(
        IDocumentStore testStore,
        IEnumerable<EntityTypeFile> models,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(testStore);
        ArgumentNullException.ThrowIfNull(models);

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
                        services.AddSpark(spark => spark.UseContext<TContext>());

                        var existing = services.Single(d => d.ServiceType == typeof(IDocumentStore));
                        services.Remove(existing);
                        services.AddSingleton(testStore);

                        configureServices?.Invoke(services);
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
    /// validation cookie (<c>.AspNetCore.Antiforgery.*</c>) and the readable <c>XSRF-TOKEN</c>
    /// cookie. Returns the raw <c>Cookie</c> header value (combining both cookies) and the
    /// <c>X-XSRF-TOKEN</c> request token to attach to mutating requests.
    /// <see cref="TestServer"/>'s HttpClient does not auto-manage cookies, so callers must thread
    /// these through explicitly — see <see cref="SparkTestClient"/> for a wrapper that does this.
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
