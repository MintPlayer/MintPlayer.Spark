using System.Text.Json.Nodes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Runs the Fleet demo app on <see cref="SparkAppTestHost"/>. Adds what only Fleet needs: the shared
/// SparkModules database and replication settings, the JWT audience and the issuer it trusts, and the
/// seeding helpers for modules.
/// </summary>
public sealed class FleetTestHost : SparkAppTestHost
{
    public static readonly SparkAppDescriptor Fleet = new(
        AppName: "Fleet",
        ProjectDirectory: Path.Combine("apps", "Fleet", "Fleet"),
        ProjectFileName: "Fleet.csproj",
        DatabasePrefix: "SparkFleetE2E",
        CoverageSlug: "fleet")
    {
        UsesMailPickup = true,
    };

    public FleetTestHost() : base(Fleet) { }

    /// <summary>
    /// Cross-module certificate enforcement. Defaults to <c>Production</c> — the strict setting, so
    /// the shared host keeps proving that an uncertificated caller is refused. A host that needs to
    /// exercise what happens <i>after</i> authentication succeeds sets <c>Development</c>, which
    /// accepts any caller naming a registered module.
    /// </summary>
    /// <remarks>
    /// <see cref="SparkAppTestHost.EnvironmentName"/> must not be <c>Development</c> for the same reason:
    /// <c>SparkReplicationCertificateMode.Auto</c> resolves to Development there, which would silently
    /// relax the certificate requirement the default host exists to prove.
    /// </remarks>
    public SparkReplicationCertificateMode CertificateMode { get; init; } = SparkReplicationCertificateMode.Production;

    private string TestModulesDatabase => $"SparkModulesE2E-{Suffix}";

    /// <summary>Base URL of the running Fleet instance (HTTPS, self-signed).</summary>
    public string FleetUrl => AppUrl;

    /// <summary>
    /// The plain-http base URL.
    /// </summary>
    public string FleetHttpUrl => AppHttpUrl;

    protected override IEnumerable<string> ExtraDatabases => [TestModulesDatabase];

    /// <summary>
    /// The issuer whose tokens Fleet accepts (<c>Spark:JwtBearer:Authority</c>), typically a running
    /// <see cref="SparkIdTestHost"/>'s http URL. Null leaves bearer tokens off, as in the shared host.
    /// </summary>
    public string? JwtBearerAuthority { get; init; }

    protected override Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context)
    {
        var spark = settings["Spark"]!.AsObject();
        spark["Replication"] = new JsonObject
        {
            ["ModuleName"] = "Fleet",
            ["ModuleUrl"] = context.HttpsUrl,
            ["SparkModulesUrls"] = new JsonArray(context.RavenUrls[0]),
            ["SparkModulesDatabase"] = TestModulesDatabase,
            ["ClientCertificate"] = new JsonObject { ["Mode"] = CertificateMode.ToString() },
        };
        if (JwtBearerAuthority is not null)
            spark["JwtBearer"] = new JsonObject { ["Authority"] = JwtBearerAuthority, ["Audience"] = "fleet-api" };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers a module in the shared SparkModules database, the way a real module registers
    /// itself at startup. Cross-module endpoints refuse a caller naming a module with no entry
    /// here, so this is the difference between "unknown module" and "known but maybe unauthorized"
    /// — which are the two refusals worth telling apart.
    /// </summary>
    /// <remarks>
    /// Writes a point-loadable document id rather than relying on a query, matching how
    /// <c>IModuleDirectory</c> looks modules up: an index would answer these authentication-gating
    /// lookups from a possibly-stale view.
    /// </remarks>
    public async Task SeedModuleAsync(string moduleName, string? clientCertificateThumbprint = null)
    {
        using var modulesStore = new DocumentStore { Urls = RavenServer.Store.Urls, Database = TestModulesDatabase };
        modulesStore.Initialize();

        var documentId = ModuleInformation.DocumentId(moduleName);
        using (var session = modulesStore.OpenAsyncSession())
        {
            await session.StoreAsync(new ModuleInformation
            {
                AppName = moduleName,
                AppUrl = $"https://localhost:1/{moduleName}",
                DatabaseName = $"{moduleName}-e2e",
                DatabaseUrls = RavenServer.Store.Urls,
                RegisteredAtUtc = DateTime.UtcNow,
                ClientCertificateThumbprint = clientCertificateThumbprint,
            }, documentId);
            await session.SaveChangesAsync();
        }

        // Read back through a fresh session. A seeding helper that silently writes nowhere turns
        // every downstream assertion into a mystery — the failure surfaces as "the endpoint
        // refused a registered module", which reads like the product is broken.
        using var verify = modulesStore.OpenAsyncSession();
        _ = await verify.LoadAsync<ModuleInformation>(documentId)
            ?? throw new InvalidOperationException(
                $"Seeded module '{moduleName}' is not readable at '{documentId}' in '{TestModulesDatabase}'. "
                + $"Modules present: {await DescribeModulesAsync()}");
    }

    /// <summary>
    /// Everything in the shared SparkModules database, for failure messages. The interesting case
    /// is a lookup that misses while the document plainly exists — which points at the two
    /// processes disagreeing about the database, not at the document.
    /// </summary>
    public async Task<string> DescribeModulesAsync()
    {
        using var modulesStore = new DocumentStore { Urls = RavenServer.Store.Urls, Database = TestModulesDatabase };
        modulesStore.Initialize();
        using var session = modulesStore.OpenAsyncSession();

        var all = await session.Advanced.AsyncRawQuery<ModuleInformation>("from @all_docs where startsWith(id(), 'moduleInformations/')").ToListAsync();
        var ids = all.Select(m => session.Advanced.GetDocumentId(m) ?? "(no id)");

        // Also sweep every other database on the server. The failure worth diagnosing is a record
        // that exists somewhere other than where the lookup reads, and naming only the expected
        // database cannot tell that apart from a record that was never written.
        var elsewhere = new List<string>();
        foreach (var name in RavenServer.Store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 100)))
        {
            if (name == TestModulesDatabase) continue;
            using var other = session.Advanced.DocumentStore.OpenAsyncSession(name);
            var found = await other.Advanced
                .AsyncRawQuery<ModuleInformation>("from @all_docs where startsWith(id(), 'moduleInformations/')")
                .ToListAsync();
            if (found.Count > 0)
                elsewhere.Add($"{name}:[{string.Join(",", found.Select(f => other.Advanced.GetDocumentId(f)))}]");
        }

        return $"db='{TestModulesDatabase}' urls=[{string.Join(",", RavenServer.Store.Urls)}] docs=[{string.Join(", ", ids)}]"
             + (elsewhere.Count > 0 ? $" ALSO-IN {string.Join(" ", elsewhere)}" : " (no module docs in any other database)");
    }

    /// <summary>
    /// Rewrites one app document's CLR-type metadata to a name no assembly in this process can
    /// resolve, then waits for indexing.
    /// <para>
    /// Reproduces the shape a document takes when it was not written by this app's session — a raw
    /// put, a bulk insert, a Smuggler import, an ETL — or when its entity type has since been
    /// renamed or moved to another assembly. RavenDB cannot recover the type on load then, which is
    /// the condition that turned a row-ruled query over a projected entity into an HTTP 500 (#281).
    /// </para>
    /// <para>
    /// Patches server-side rather than through a session, and verifies the read-back. A session
    /// write does not work here: the client re-derives <c>Raven-Clr-Type</c> from the entity it is
    /// serializing, so it silently overwrites the value and the caller's test passes having proved
    /// nothing. A helper that can quietly do nothing is the same failure this test exists to catch,
    /// so it throws instead.
    /// </para>
    /// </summary>
    public async Task SetUnresolvableClrTypeAsync(string documentId)
    {
        const string ghostType = "Ghost.Fleet.Entities.Car, Ghost.Fleet";

        using var appStore = OpenAppStore();

        var status = await appStore.Operations.SendAsync(new PatchOperation(
            documentId,
            changeVector: null,
            new PatchRequest
            {
                Script = "this['@metadata']['Raven-Clr-Type'] = args.clrType;",
                Values = { ["clrType"] = ghostType },
            }));

        if (status is not (PatchStatus.Patched or PatchStatus.NotModified))
            throw new InvalidOperationException($"Patching '{documentId}' in '{TestDatabase}' returned {status}.");

        using (var session = appStore.OpenAsyncSession())
        {
            var reloaded = await session.LoadAsync<object>(documentId)
                ?? throw new InvalidOperationException($"Document '{documentId}' not found in '{TestDatabase}'.");
            var actual = session.Advanced.GetMetadataFor(reloaded)
                .GetString(Raven.Client.Constants.Documents.Metadata.RavenClrType);
            if (actual != ghostType)
                throw new InvalidOperationException(
                    $"Expected '{documentId}' to carry CLR type '{ghostType}' but it carries '{actual}'.");
        }

        await appStore.WaitForIndexingAsync(TestDatabase);
    }
}
