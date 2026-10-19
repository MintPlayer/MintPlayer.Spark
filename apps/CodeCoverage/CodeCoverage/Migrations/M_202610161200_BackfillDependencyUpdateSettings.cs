using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Writes the dependency updater's settings (docs/code-coverage/dependency-updates-PRD.md §4.1) onto every account and
/// repository document that predates them, with their defaults.
/// </summary>
/// <remarks>
/// <para>
/// Reading needs no migration: every new enum has its default as the zero value, and the one integer
/// (<c>DependencyScheduleHour</c>, default 6) has a property initializer, so an old document deserializes to the defaults.
/// <b>Querying does.</b> An absent field matches neither <c>== 6</c> nor <c>== false</c> in RQL, so the scheduler (N5)
/// selecting the repositories due at an hour would silently skip every document written before this release.
/// </para>
/// <para>
/// Constant RQL, nothing spliced. Idempotent: a document that already carries <c>DependencyScheduleHour</c> is left as it
/// is, so a replay or a restored backup changes nothing an owner set.
/// </para>
/// </remarks>
public partial class M_202610161200_BackfillDependencyUpdateSettings : ISparkMigration
{

    public static string? Description =>
        "Accounts and repositories get the dependency updater's settings with their defaults, so they can be queried";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        // RQL strings rather than typed references, as in M_202609092000: a migration is a fact about a database at a
        // moment in time, and an enum renamed later must not change what this one meant.
        await PatchAsync("""
            from Accounts as a update {
                if (a.DependencyScheduleHour === undefined) {
                    a.DependencyUpdates = false;
                    a.DependencyScheduleFrequency = "Weekly";
                    a.DependencyScheduleWeekday = "Monday";
                    a.DependencyScheduleHour = 6;
                    a.DependencyTimeZone = "UTC";
                    a.DependencyCooldown = "ThreeDays";
                    a.DependencyBisectBudget = "Two";
                    a.DependencyRules = [];
                }
            }
            """, cancellationToken);

        await PatchAsync("""
            from Repositories as r update {
                if (r.DependencyScheduleHour === undefined) {
                    r.DependencyUpdates = "Inherit";
                    r.DependencySchedulePolicy = "Inherit";
                    r.DependencyScheduleFrequency = "Weekly";
                    r.DependencyScheduleWeekday = "Monday";
                    r.DependencyScheduleHour = 6;
                    r.DependencyCooldown = "Inherit";
                    r.DependencyBisectBudget = "Inherit";
                }
            }
            """, cancellationToken);
    }

    private async Task PatchAsync(string query, CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = query },
                // Wait for the index rather than throw "Index is stale": a bulk operation on a stale index is refused outright.
                new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);

        // Wait, so a throw aborts startup and the migration is retried on the next start rather than marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
