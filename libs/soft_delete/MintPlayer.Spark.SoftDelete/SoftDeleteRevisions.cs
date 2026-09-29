using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;

namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// The revisions half of a purge. <c>DeleteRevisionsOperation</c> is a RavenDB <b>database-admin</b>
/// operation, while deleting the document is not — so on a secured server a client certificate
/// without admin access could delete the document and then fail on its revisions, leaving the history
/// (the personal data a GDPR purge exists to remove) of a row that is gone.
/// </summary>
internal interface ISoftDeleteRevisions
{
    /// <summary>
    /// Proves the store may delete revisions, before anything is deleted. Throws
    /// <see cref="InvalidOperationException"/> when it may not; the purge is then refused with the
    /// document untouched. A success is remembered for the process.
    /// </summary>
    Task EnsureCanDeleteAsync();

    /// <summary>Deletes every revision of <paramref name="id"/>, force-created ones included. Returns how many.</summary>
    Task<long> DeleteAsync(string id);
}

/// <inheritdoc />
/// <remarks>
/// The probe is the real operation on an id that names nothing: the same endpoint, the same
/// authorization, no effect (0 revisions deleted). Cheaper and more honest than guessing the
/// certificate's clearance from a separate endpoint.
/// </remarks>
internal sealed partial class RavenSoftDeleteRevisions : ISoftDeleteRevisions
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<RavenSoftDeleteRevisions> logger;

    private volatile bool verified;

    public async Task EnsureCanDeleteAsync()
    {
        if (verified)
            return;

        try
        {
            await documentStore.Maintenance.SendAsync(
                new DeleteRevisionsOperation($"spark-purge-probe/{Guid.NewGuid():N}", removeForceCreatedRevisions: true));
            verified = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Purge refused: deleting revisions failed on a probe, so the document was not deleted. " +
                "DeleteRevisionsOperation is a RavenDB database-admin operation — give the client certificate " +
                "database-admin access on '{Database}' (see the SoftDelete README).", documentStore.Database);
            throw new InvalidOperationException(
                "Purge refused before deleting anything: this RavenDB connection may not delete revisions " +
                "(DeleteRevisionsOperation needs database-admin access). See the SoftDelete README.", ex);
        }
    }

    public async Task<long> DeleteAsync(string id)
    {
        // After the document, never before: deleting revisions first and the document second writes a
        // fresh delete revision (measured, #460 spike H1). Force-created revisions only go with the flag.
        var result = await documentStore.Maintenance.SendAsync(new DeleteRevisionsOperation(id, removeForceCreatedRevisions: true));
        return result.TotalDeletes;
    }
}
