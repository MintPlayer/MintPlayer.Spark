using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Renames <c>EventColumnMapping.AddLinkedIfMissing</c> to <c>AutoAddToBoard</c> on every stored
/// rule, and gives it the new default of <see langword="true"/>.
///
/// The rename is not cosmetic — the two fields mean different things. The old one asked "when a
/// linked issue is missing, recruit it?", scoped to the linked-issue pass alone; the new one asks
/// "may this rule put an item on the board?" and governs the event's own subject too, which used to
/// be hard-coded per item type (issues always added, pull requests never). See C15 in
/// docs/decisions_messaging_and_project_automation.md.
///
/// This migration exists because doing nothing would be wrong in a way nothing reports. Every rule
/// saved before the rename has no <c>AutoAddToBoard</c> member, and an absent JSON boolean
/// deserialises to <c>false</c> — NOT to the C# property initialiser's <c>true</c>, which only ever
/// runs for a newly constructed object. So "just let the default apply" would silently opt every
/// existing rule OUT of board membership, turning `IssuesOpened` rules that have been adding cards
/// all along into rules that quietly move nothing.
///
/// Existing rules therefore get <c>true</c>: that is the new default, and it is also what they
/// effectively did for the item the event was about, since the issue path was hard-coded to add.
/// The old value is deliberately NOT carried over — it answered a different question, and it was
/// `false` on virtually every rule because that was its default for the few days it existed.
///
/// It also backfills <c>MoveLinkedIssues</c>, which #377 added with a C# default of
/// <see langword="true"/> and no migration. Measured against the development database 2026-09-08:
/// the one configured rule carries neither member, so it has been loading as
/// <c>MoveLinkedIssues = false</c> — the field's default never applied to it, and the
/// linked-issue movement that #377 existed to deliver has never once run for that board. The same
/// absent-boolean trap, one field earlier.
///
/// Idempotent, and replay-safe in the direction that matters: each value is only written when the
/// member is absent, so a retry cannot overwrite a choice the user has since made in the UI.
/// </summary>
public partial class M_202609081200_RenameAddLinkedIfMissing : ISparkMigration
{
    public static long Version => 202609081200;
    public static string? Description => "Rename EventColumnMapping.AddLinkedIfMissing to AutoAddToBoard and backfill both booleans to true";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // EventMappings is an embedded array, so this is a per-element patch rather than a
                // field rename. Guarded on the array's presence: a board that has never been
                // configured has no EventMappings member at all.
                Query = """
                    from GitHubProjects update {
                        if (this.EventMappings) {
                            for (var i = 0; i < this.EventMappings.length; i++) {
                                var rule = this.EventMappings[i];
                                if (rule.AutoAddToBoard === undefined) {
                                    rule.AutoAddToBoard = true;
                                }
                                if (rule.MoveLinkedIssues === undefined) {
                                    rule.MoveLinkedIssues = true;
                                }
                                delete rule.AddLinkedIfMissing;
                            }
                        }
                    }
                    """,
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
