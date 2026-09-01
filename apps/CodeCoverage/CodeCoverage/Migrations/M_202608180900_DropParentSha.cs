using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Deletes every stored <c>Commit.ParentSha</c>.
///
/// The field had two writers that disagreed about what it meant — the push
/// webhook wrote the previous ref tip, the upload wrote a PR base — and webhook
/// delivery is unordered, so a push landing after an upload replaced a PR base
/// with a ref tip. Existing documents therefore hold an unclassifiable mixture
/// of PR bases, ref tips, all-zero shas from branch creations, and nulls, with
/// nothing to tell them apart. No transform can recover a trustworthy value, so
/// the honest operation is to drop them all and let the single remaining writer
/// (the pull_request webhook) repopulate correct ones.
///
/// Nothing reads the field programmatically, so this is hygiene rather than a
/// fix — the fix is the writers. It runs anyway because a plausible wrong value
/// is worse than no value, and it replays automatically on a restored backup or
/// a fresh environment, which a hand-run patch in Raven Studio would not.
/// </summary>
public partial class M_202608180900_DropParentSha : ISparkMigration
{
    public static long Version => 202608180900;
    public static string? Description => "Drop Commit.ParentSha: two writers left an unclassifiable mixture";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = "from Commits update { delete this.ParentSha; }",
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on
        // the next start rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
