using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Gives every account that predates PR size labels (docs/coverage_pr_size_labels_PRD.md D2, D5) the default scale and
/// ignore rules, the ones <see cref="Entities.PrSizeDefaults"/> gives a new account. The feature itself stays off.
/// </summary>
/// <remarks>
/// Repositories need nothing: a missing <c>PrSizeLabels</c> reads as <c>Inherit</c>, the enum's zero, and nothing queries it.
/// Constant RQL, nothing spliced; idempotent: an account that already carries a scale (even an emptied one) keeps it.
/// The literal rows must match <see cref="Entities.PrSizeDefaults"/>; a test compares them.
/// </remarks>
public partial class M_202610241200_BackfillPrSizeLabelDefaults : ISparkMigration
{
    public static long Version => 202610241200;

    public static string? Description =>
        "Accounts get the default PR size label scale and ignore rules";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await PatchAsync("""
            from Accounts as a update {
                if (a.PrSizeLabels === undefined) {
                    a.PrSizeLabels = false;
                }
                if (a.PrSizeLabelScale === undefined) {
                    a.PrSizeLabelScale = [
                        { Id: "xs", Name: "size/XS", MinLines: 0, Color: "#009900", Description: null },
                        { Id: "s", Name: "size/S", MinLines: 10, Color: "#77bb00", Description: null },
                        { Id: "m", Name: "size/M", MinLines: 30, Color: "#eebb00", Description: null },
                        { Id: "l", Name: "size/L", MinLines: 100, Color: "#ee9900", Description: null },
                        { Id: "xl", Name: "size/XL", MinLines: 500, Color: "#ee5500", Description: null },
                        { Id: "xxl", Name: "size/XXL", MinLines: 1000, Color: "#ee0000", Description: null }
                    ];
                }
                if (a.PrSizeIgnoreRules === undefined) {
                    a.PrSizeIgnoreRules = [
                        { Id: "json", Glob: "**/*.json", Mode: "IgnoreAdditions" },
                        { Id: "tests", Glob: "**/*Tests/**", Mode: "IgnoreAdditions" },
                        { Id: "package-lock", Glob: "**/package-lock.json", Mode: "Ignore" },
                        { Id: "packages-lock", Glob: "**/packages.lock.json", Mode: "Ignore" },
                        { Id: "directory-packages", Glob: "**/Directory.Packages.props", Mode: "Ignore" },
                        { Id: "markdown", Glob: "**/*.md", Mode: "Ignore" }
                    ];
                }
            }
            """, cancellationToken);
    }

    private async Task PatchAsync(string query, CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = query }, new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
