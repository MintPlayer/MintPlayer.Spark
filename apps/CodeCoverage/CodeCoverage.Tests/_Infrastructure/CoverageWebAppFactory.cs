using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Raven.Client.Documents;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// Boots the real <c>apps/CodeCoverage</c> composition root in-process, against an embedded
/// RavenDB, so requests travel the actual middleware pipeline.
/// <para>
/// This exists because of a gap that was measured rather than guessed: <b>no test in this
/// application had ever executed a <c>[SparkAuthorize]</c> filter</b>. Every controller test
/// constructs the controller through DI and calls the method directly, which is useful for the
/// logic inside the method and proves exactly nothing about the attribute guarding it — the
/// filter is part of the pipeline, and calling a method never runs a pipeline. In this app's
/// own coverage report <c>SparkAuthorizeAttribute</c> and <c>SparkAuthorizeHandler</c> sat at
/// <c>line-rate="0"</c> while six production files carried the attribute. It runs a live
/// website, so "we assume the attribute works" is not a position worth holding.
/// </para>
/// <para>
/// It also covers <c>Program.cs</c> — 291 lines at 0%, the single largest uncovered file in the
/// repository — as a by-product. Unit tests are the wrong instrument for a composition root; a
/// boot is the right one, and it asserts something real (the app starts at all) rather than
/// being a coverage-farming exercise.
/// </para>
/// </summary>
/// <remarks>
/// Three things the real Program demands before it will start, all supplied here:
/// <list type="bullet">
/// <item>a GitHub ClientId/ClientSecret — it throws by design if sign-in is unconfigured,
/// because GitHub is the only authentication provider it registers (D5, "fail loud");</item>
/// <item>a RavenDB URL, pointed at the embedded test server rather than localhost:8080;</item>
/// <item>an environment that is <b>not</b> Development, or the SPA middleware would spawn
/// <c>npm start</c> and proxy an Angular dev server for every test run.</item>
/// </list>
/// The configuration keys are environment-prefixed (<c>GitHub:{Environment}:ClientId</c>), so
/// the environment name below and the keys have to agree — that coupling is Program's, not this
/// class's.
/// </remarks>
public sealed class CoverageWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Not "Development": that would start the Angular CLI dev server through
    /// <c>UseAngularCliServer</c>. Not "Production" either — the name is used as the
    /// configuration prefix, so it stays distinct from anything a real deployment uses.
    /// </summary>
    public const string EnvironmentName = "IntegrationTest";

    private readonly string _ravenUrl;
    private readonly string _database;

    public CoverageWebAppFactory(IDocumentStore store)
    {
        _ravenUrl = store.Urls[0];
        _database = store.Database;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);

        // The app's PROJECT directory, not the test assembly's output directory.
        //
        // Spark reads App_Data from the content root: security.json, the model JSON, and
        // modelHashes.json. WebApplicationFactory defaults the content root somewhere that has
        // none of them, so `ModelHashFile.Read` returned null, the expected hash did not match
        // the computed one, and the host threw SparkModelOutOfSyncException before serving
        // anything -- while `--spark-verify-model` reported the model perfectly in sync,
        // because it runs from the project directory.
        builder.UseContentRoot(AppProjectDirectory());

        // UseSetting, not ConfigureAppConfiguration. Program reads `builder.Configuration`
        // while it is still registering services -- the GitHub ClientId check runs inside
        // AddSpark -- and an AddInMemoryCollection source added through
        // ConfigureAppConfiguration is not visible that early, so the app threw
        // "GitHub sign-in is not configured" before a single test ran. UseSetting lands in
        // host configuration, which `builder.Configuration` already includes.
        builder.UseSetting("Spark:RavenDb:Urls:0", _ravenUrl);
        builder.UseSetting("Spark:RavenDb:Database", _database);
        builder.UseSetting("Coverage:BaseUrl", "http://localhost");

        // Placeholders, and deliberately obvious ones. The OAuth handler is registered but
        // never reached: these tests assert what happens BEFORE any redirect to GitHub, and a
        // test needing a real token would be an integration test against GitHub, which is a
        // different thing entirely.
        builder.UseSetting($"GitHub:{EnvironmentName}:ClientId", "integration-test-client-id");
        builder.UseSetting($"GitHub:{EnvironmentName}:ClientSecret", "integration-test-client-secret");
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root and back down to the app project.
    /// </summary>
    /// <remarks>
    /// Anchored on a file that only the repository root has, rather than counting <c>..</c>
    /// segments: the number of levels differs between a local <c>bin/Debug/net10.0</c> and
    /// whatever a CI runner or a future TFM produces, and a miscount would fail as
    /// "model out of sync", which points at the model and not at the path.
    /// </remarks>
    private static string AppProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                $"Could not locate the repository root above {AppContext.BaseDirectory}; "
                + "expected an ancestor directory containing nx.json.");

        var app = Path.Combine(dir.FullName, "apps", "CodeCoverage", "CodeCoverage");

        if (!File.Exists(Path.Combine(app, "App_Data", "security.json")))
            throw new InvalidOperationException(
                $"Resolved the app content root to '{app}', but it has no App_Data/security.json. "
                + "Spark reads its model and security configuration from the content root.");

        return app;
    }
}
