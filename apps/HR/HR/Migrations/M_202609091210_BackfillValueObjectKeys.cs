using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace HR.Migrations;

/// <summary>
/// Gives every stored embedded row in this database a key: <c>Person.Jobs</c>, and the identity
/// provider's <c>OidcApplication.Secrets</c> and <c>.Claims</c>.
///
/// See <c>CodeCoverage.Migrations.M_202609091200_BackfillBuildSessionKeys</c> for why a keyless row
/// cannot be recognised once loaded, and therefore why this has to run before the property can do
/// any harm.
/// </summary>
/// <remarks>
/// ⚠️ <b>The identity-provider collections are patched from here, not from the package.</b>
/// Migration discovery is a <c>SyntaxProvider</c>, so it only finds <c>ISparkMigration</c> classes
/// declared in the compilation being built — a migration shipped inside
/// <c>MintPlayer.Spark.IdentityProvider</c> would never be found by any host. HR is the only app
/// whose context exposes <c>OidcApplications</c>, so this is the one database that has them.
/// <para>
/// The alternative is the manual escape hatch, <c>AddMigrations(b =&gt; b.AddMigration&lt;T&gt;())</c>,
/// called from the package's own registration. That is the better answer once a second host exists;
/// with one host it buys indirection and nothing else.
/// </para>
/// <para>
/// ⚠️ <b>RQL strings, never typed references — deliberately.</b> A migration is a historical fact
/// about a database at a moment in time; an entity class is a live thing that keeps changing. If
/// this named <c>BuildSession</c> or <c>Sessions</c> as symbols, then renaming or deleting either
/// later would break the build of a migration that has already run everywhere and must never run
/// differently. Worse than a broken build, a rename that still compiles would silently change what
/// an already-applied migration means.
/// <para>
/// So the collection and property names are strings, frozen at the shape the database had when this
/// was written. The cost is that a typo is not caught by the compiler; the test suite and the
/// startup gate are what catch it instead.
/// </para>
/// </remarks>
public partial class M_202609091210_BackfillValueObjectKeys : ISparkMigration
{
    public static long Version => 202609091210;
    public static string? Description => "Give stored CarreerJob, ClientSecret and ClientClaim rows a key";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        // ⚠️ No guid helper exists in RavenDB's patch engine (measured), so the key is derived from
        // the document id plus the array index: unique, and reproducible on a replay. `if (rows)`
        // guards an array absent from older JSON; `if (!rows[i].Id)` keeps the patch idempotent and
        // preserves a key that is already there.
        await PatchAsync("from People as d update { %BODY% }", "Jobs", cancellationToken);
        await PatchAsync("from OidcApplications as d update { %BODY% }", "Secrets", cancellationToken);
        await PatchAsync("from OidcApplications as d update { %BODY% }", "Claims", cancellationToken);
    }

    private async Task PatchAsync(string shape, string arrayProperty, CancellationToken cancellationToken)
    {
        var body = $$"""
            var rows = d.{{arrayProperty}};
            if (rows) {
                for (var i = 0; i < rows.length; i++) {
                    if (!rows[i].Id) {
                        rows[i].Id = id(d).replace(/[^A-Za-z0-9]/g, '') + '{{arrayProperty}}' + i.toString();
                    }
                }
            }
            """;

        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = shape.Replace("%BODY%", body) }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
