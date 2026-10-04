using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Session;
using Sparrow.Json;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The document change vector behind every row of a page (#467, D14): what the grid sends back as the
/// row's etag when it deletes it, so a delete from a stale grid is a 409 rather than last-write-wins.
/// </summary>
/// <remarks>
/// Read as metadata only, in one request per page, for every row shape alike — entities, index
/// projections, custom and streamed queries. A projection is not tracked by the session, so
/// <c>GetChangeVectorFor</c> throws on it (spike S5b); its query metadata does carry the document's
/// change vector, but a lookup by id covers every path through one place instead of each
/// materialisation. A row with no document behind it (a computed row) gets no etag, and cannot be
/// deleted from the grid. The lookup runs right after the query, so an edit landing between the two
/// reads hands the grid a newer etag than its values — a window of one round trip, against the minutes
/// a grid stays open, which is the race D14 closes.
/// </remarks>
internal static class RowChangeVectors
{
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        IAsyncDocumentSession session, IEnumerable<string?> ids, CancellationToken cancellationToken = default)
    {
        var distinct = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (distinct.Length == 0)
            return result;

        var executor = session.Advanced.RequestExecutor;
        var command = new GetDocumentsCommand(session.Advanced.DocumentStore.Conventions, distinct!, includes: null, metadataOnly: true);
        using (executor.ContextPool.AllocateOperationContext(out JsonOperationContext context))
        {
            await executor.ExecuteAsync(command, context, sessionInfo: null, cancellationToken);
            var results = command.Result?.Results;
            if (results is null)
                return result;

            foreach (var item in results)
            {
                if (item is not BlittableJsonReaderObject document
                    || !document.TryGet(Raven.Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata)
                    || !metadata.TryGet(Raven.Client.Constants.Documents.Metadata.Id, out string id)
                    || !metadata.TryGet(Raven.Client.Constants.Documents.Metadata.ChangeVector, out string changeVector))
                    continue;
                result[id] = changeVector;
            }
        }

        return result;
    }
}
