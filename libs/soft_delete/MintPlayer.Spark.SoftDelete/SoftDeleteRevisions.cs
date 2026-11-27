using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// The revisions half of a purge. On RavenDB, deleting revisions is a <b>database-admin</b> operation, while deleting the
/// document is not, so a client certificate without admin access could delete the document and then fail on its
/// revisions, leaving the history (the personal data a GDPR purge exists to remove) of a row that is gone.
/// </summary>
internal interface ISoftDeleteRevisions
{
    /// <summary>
    /// Proves the store may delete revisions, before anything is deleted. Throws
    /// <see cref="InvalidOperationException"/> when it may not; the purge is then refused with the
    /// document untouched.
    /// </summary>
    Task EnsureCanDeleteAsync();

    /// <summary>Deletes every revision of <paramref name="id"/>. Returns how many.</summary>
    Task<long> DeleteAsync(Type entityType, string id);
}

/// <inheritdoc />
/// <remarks>Over the store's <see cref="ISparkRevisions"/> (#536 S8): RavenDB's probe is the real operation on an id that
/// names nothing; the relational and in-memory stores write their own revisions and may always delete them.</remarks>
internal sealed partial class SoftDeleteRevisions : ISoftDeleteRevisions
{
    [Inject] private readonly ISparkRevisions revisions;
    [Inject] private readonly ILogger<SoftDeleteRevisions> logger;

    public async Task EnsureCanDeleteAsync()
    {
        try
        {
            await revisions.EnsureCanDeleteAsync();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Purge refused: deleting revisions failed on a probe, so the document was not deleted (see the SoftDelete README).");
            throw new InvalidOperationException(
                "Purge refused before deleting anything: this connection may not delete revisions " +
                "(on RavenDB, DeleteRevisionsOperation needs database-admin access). See the SoftDelete README.", ex);
        }
    }

    public Task<long> DeleteAsync(Type entityType, string id) => revisions.DeleteAsync(entityType, id);
}
