using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Gives every account that predates package versioning (docs/coverage_package_versioning_PRD.md D12, D14, D15) the
/// defaults a new account gets: title correction off, Releases and auto-cut release branches on.
/// </summary>
/// <remarks>
/// Needed because the two "on" defaults would otherwise read false from a stored document without the field.
/// Repositories need nothing: their policies read <c>Inherit</c>, the enum's zero. Constant RQL, nothing spliced; idempotent.
/// </remarks>
public partial class M_202610241300_BackfillVersioningDefaults : ISparkMigration
{
    public static long Version => 202610241300;

    public static string? Description =>
        "Accounts get the package-versioning defaults: title correction off, Releases and release branches on";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = """
                    from Accounts as a update {
                        if (a.PrTitleCorrection === undefined) { a.PrTitleCorrection = false; }
                        if (a.ReleaseOnTag === undefined) { a.ReleaseOnTag = true; }
                        if (a.AutoCutReleaseBranches === undefined) { a.AutoCutReleaseBranches = true; }
                    }
                    """,
            }, new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
