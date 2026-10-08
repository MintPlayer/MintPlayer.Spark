using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Shared;

/// <summary>
/// Boots a demo application's real <c>Program</c> in-process, against an embedded RavenDB, far enough
/// that its route table exists, and compares that with the committed snapshot
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0). Linked into each <c>apps/{App}/{App}.Tests</c>.
/// </summary>
/// <typeparam name="TAppType">Any type of the application's own assembly; it only locates the entry point.</typeparam>
/// <remarks>
/// <list type="bullet">
/// <item><b>One application per test process.</b> Spark composes the library layers once per process,
/// from the process's whole dependency closure (<c>SparkLayerCatalog</c>, a static <c>Lazy</c>). A
/// process that references two applications composes their union: hosting Fleet beside QnA made
/// Fleet demand QnA's moderation bindings. Hence one test project per application.</item>
/// <item><b>Not Development.</b> Every app calls <c>UseAngularCliServer</c> under
/// <c>IsDevelopment()</c>, which spawns <c>npm start</c>. It is also why the identity provider needs a
/// signing key file: it generates one only in Development.</item>
/// <item><b>The app's project directory as content root</b>, so Spark reads the app's real
/// <c>App_Data</c> (model, security.json, model hashes), the same anchoring CodeCoverage.Tests uses.</item>
/// <item><b>No hosted services but the web host.</b> Background workers (messaging, replication,
/// cron, subscriptions) start after the route table is final and cannot change it, while they can
/// reach for resources a snapshot has no business touching. <c>GenericWebHostService</c> stays: it is
/// what builds the pipeline.</item>
/// <item>Settings go through <c>UseSetting</c> (host configuration), because <c>Program</c> reads
/// <c>builder.Configuration</c> while it is still registering services.</item>
/// </list>
/// </remarks>
public sealed class SparkAppRouteHost<TAppType>(
    string appDirectory,
    IDocumentStore store,
    IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<TAppType>
    where TAppType : class
{
    public const string EnvironmentName = "RouteSnapshot";

    /// <summary>Boots the app and asserts its route table matches <paramref name="fixture"/> (repository-relative).</summary>
    public static async Task AssertSnapshotAsync(
        string appDirectory,
        IDocumentStore store,
        IReadOnlyDictionary<string, string?> settings,
        string fixture)
    {
        await using var host = new SparkAppRouteHost<TAppType>(appDirectory, store, settings);

        RouteTableSnapshot.AssertMatchesFixture(RouteTableSnapshot.Capture(host.Services), fixture);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseContentRoot(AppProjectDirectory(appDirectory));

        builder.UseSetting("Spark:RavenDb:Urls:0", store.Urls[0]);
        builder.UseSetting("Spark:RavenDb:Database", store.Database);
        builder.UseSetting("Spark:DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), $"spark-app-route-tests-dataprotection-{Environment.ProcessId}"));
        builder.UseSetting("Spark:HttpsRedirection", "false");

        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            var background = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType?.Name != "GenericWebHostService")
                .ToList();
            foreach (var descriptor in background)
                services.Remove(descriptor);
        });
    }

    /// <summary>The app's project directory (<c>apps/{relative}</c>), found by walking up to <c>nx.json</c>.</summary>
    private static string AppProjectDirectory(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"Could not locate the repository root (nx.json) above {AppContext.BaseDirectory}.");

        var app = Path.Combine(dir.FullName, "apps", relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(Path.Combine(app, "App_Data", "security.json")))
            throw new InvalidOperationException($"'{app}' has no App_Data/security.json; Spark reads its model and security from the content root.");

        return app;
    }
}

/// <summary>Signing keys for an app whose identity provider runs outside Development.</summary>
public static class SparkAppSigningKey
{
    /// <summary>
    /// An RSA key in the shape the identity provider reads (base64url RSA parameters), in a temp file.
    /// Outside Development the provider requires one rather than generating it.
    /// </summary>
    public static string NewFile()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var p = rsa.ExportParameters(includePrivateParameters: true);
        static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var path = Path.Combine(Path.GetTempPath(), $"spark-app-route-tests-key-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["N"] = B64(p.Modulus!), ["E"] = B64(p.Exponent!), ["D"] = B64(p.D!), ["P"] = B64(p.P!),
            ["Q"] = B64(p.Q!), ["DP"] = B64(p.DP!), ["DQ"] = B64(p.DQ!), ["QI"] = B64(p.InverseQ!),
        }));
        return path;
    }
}
