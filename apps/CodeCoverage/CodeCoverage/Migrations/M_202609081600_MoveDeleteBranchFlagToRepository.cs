using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Deletes the orphaned <c>DeleteBranchOnPrClose</c> member from every stored
/// <c>GitHubProject</c>. The setting now lives on <see cref="Entities.Repository"/>.
///
/// Removing the C# property and its model-JSON block changes neither the documents already in
/// RavenDB — synchronize never deletes — so without this the member lingers in every board
/// document, where the next person to open Raven Studio would reasonably read it as live
/// configuration. Same reasoning as <see cref="M_202608180900_DropParentSha"/>: hygiene rather than
/// a fix, run anyway because a plausible wrong value is worse than no value, and it replays on a
/// restored backup or a fresh environment, which a hand-run patch in Studio would not.
///
/// <para>
/// Nothing ever read the flag, so no behaviour changes when it goes. It was declared in #369 per
/// decisions A4/D3, but the same pull request deleted the only implementation that had ever acted
/// on it (<c>apps/WebhooksDemo/Recipients/DeleteBranchOnPullRequestClose.cs</c>, removed in
/// <c>2e30b582</c>), and the milestone that was meant to reimplement it behind the flag was closed
/// without doing so. See C16 in <c>docs/decisions_messaging_and_project_automation.md</c>.
/// </para>
///
/// <para>
/// <b>The value is deliberately deleted rather than carried across to the matching repository.</b>
/// Migrating a stored <c>true</c> would look considerate and would be the dangerous choice: the flag
/// has never done anything, so a <c>true</c> in the database records someone ticking a box that was
/// inert. Honouring it now would silently arm irreversible branch deletion on a real repository on
/// the strength of consent given to a no-op. A destructive capability gets opted into deliberately,
/// after it works.
/// </para>
///
/// Idempotent: deleting an absent member is a no-op, so a replay cannot damage a board that has
/// since been reconfigured.
/// </summary>
public partial class M_202609081600_MoveDeleteBranchFlagToRepository : ISparkMigration
{
    public static long Version => 202609081600;
    public static string? Description => "Drop the orphaned GitHubProject.DeleteBranchOnPrClose; the setting moved to Repository";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = "from GitHubProjects update { delete this.DeleteBranchOnPrClose; }",
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
