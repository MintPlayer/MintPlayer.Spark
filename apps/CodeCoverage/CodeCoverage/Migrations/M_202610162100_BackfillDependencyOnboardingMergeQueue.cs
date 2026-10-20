using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Writes the "Onboarding adds the merge queue" setting (dependency-updates PRD R25, R26) onto every account and repository
/// document that predates it, with its default: Yes on the account, "use the account setting" on the repository.
/// </summary>
/// <remarks>
/// Reading needs no migration (both zero values are the defaults); querying does, because an absent field matches no
/// value in RQL (see <see cref="M_202610161200_BackfillDependencyUpdateSettings"/>). Constant RQL, nothing spliced;
/// idempotent: a document that already carries the field keeps what it has.
/// </remarks>
public partial class M_202610162100_BackfillDependencyOnboardingMergeQueue : ISparkMigration
{

    public static string? Description =>
        "Accounts and repositories get the onboarding merge-queue setting with its default, so they can be queried";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await PatchAsync("""
            from Accounts as a update {
                if (a.DependencyOnboardingMergeQueue === undefined) {
                    a.DependencyOnboardingMergeQueue = "Yes";
                }
            }
            """, cancellationToken);

        await PatchAsync("""
            from Repositories as r update {
                if (r.DependencyOnboardingMergeQueue === undefined) {
                    r.DependencyOnboardingMergeQueue = "Inherit";
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
