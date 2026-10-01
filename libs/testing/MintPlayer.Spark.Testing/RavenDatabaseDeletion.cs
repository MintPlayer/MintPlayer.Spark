using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Makes a <c>RavenTestDriver</c> store hard-delete its database WITHOUT waiting for the server's
/// deletion confirmation. The one place every RavenTestDriver base in this repository gets it from.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="DeleteOnDispose"/> from the driver's <c>PreInitialize</c> override, which
/// <c>RavenTestDriver.GetDocumentStore()</c> runs for EVERY store it creates — the fixture's own
/// <c>Store</c> and every inline <c>using var store = GetDocumentStore();</c> in a test alike.
/// Users: <c>SparkTestDriver</c>, <c>SparkSharedDatabase</c>, and (linked by source)
/// <c>apps/CodeCoverage/CodeCoverage.Tests/CoverageRavenTest</c>.
/// </para>
/// <para>
/// <b>Why.</b> <c>RavenTestDriver</c>'s own <c>AfterDispose</c> handler sends
/// <c>DeleteDatabasesOperation(name, hardDelete: true)</c> with no <c>timeToWaitForConfirmation</c>,
/// which the server resolves to a <b>hard-coded</b> 15 s wait (<c>WaitForDeletionToComplete</c>) that
/// no server configuration reaches. Under load that wait is slow and, at 15 s, a teardown failure.
/// Sending the operation ourselves first, from <c>BeforeDispose</c> (which the store raises before
/// <c>AfterDispose</c>), is the only way to set the budget; the driver's delete then runs anyway and
/// its handler swallows the resulting <c>DatabaseDoesNotExistException</c>. See
/// <c>SparkTestDriver.DisposeAsync</c> for the measured history, including approaches already rejected.
/// </para>
/// <para>
/// ⚠️ <b>Zero, and it has to be zero</b> — not a short timeout. With zero, the server's
/// <c>remaining = timeout - elapsed</c> is already negative, so the confirmation wait is never
/// entered. The raft command is still submitted and the database is still deleted; only the
/// acknowledgement is skipped. A longer budget does not make deletion faster; it makes teardown
/// block for that long instead.
/// </para>
/// <para>
/// Linked by source (not referenced) into <c>apps/CodeCoverage/CodeCoverage.Tests</c>, like
/// <c>RavenServerLocator</c>, so it must depend on nothing but the RavenDB client.
/// </para>
/// </remarks>
public static class RavenDatabaseDeletion
{
    private static readonly TimeSpan DeletionBudget = TimeSpan.Zero;

    /// <summary>
    /// Subscribes <paramref name="store"/>'s <c>BeforeDispose</c> to a zero-wait hard delete of its
    /// database. The handler never throws.
    /// </summary>
    /// <remarks>
    /// <c>BeforeDispose</c> lives on <see cref="DocumentStoreBase"/>, not on the interface;
    /// <c>RavenTestDriver.GetDocumentStore()</c> always creates a <c>DocumentStore</c>. Any other
    /// store is left on the driver's default delete.
    /// </remarks>
    public static void DeleteOnDispose(IDocumentStore store)
    {
        if (store is DocumentStoreBase documentStore)
            documentStore.BeforeDispose += (_, _) => DeleteWithoutWaiting(store);
    }

    private static void DeleteWithoutWaiting(IDocumentStore store)
    {
        try
        {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(
                store.Database,
                hardDelete: true,
                fromNode: null,
                timeToWaitForConfirmation: DeletionBudget));
        }
        catch (Exception)
        {
            // ⚠️ Never fail a test in teardown over cleanup: the database lives on a server that dies
            // with the process, whereas throwing here REPLACES the real result of the test that ran.
            // DatabaseDoesNotExistException (already gone) lands here too.
        }
    }
}
