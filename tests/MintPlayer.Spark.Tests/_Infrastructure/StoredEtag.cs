using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// The etag a client holding the stored version would send: the document's change vector, which
/// <c>/po/update</c>, <c>/po/delete</c>, <c>/po/purge</c> and <c>/po/delete-many</c> compare against
/// (#467, D14/D16). Read from the store rather than through <c>/po/load</c>, so it also works for a
/// soft-deleted row the load no longer shows.
/// </summary>
internal static class StoredEtag
{
    /// <summary>An etag for a row that does not exist; the request is refused before it is compared.</summary>
    public const string ForMissingRow = "A:1-missing";

    public static async Task<string> OfAsync(IDocumentStore store, string id)
    {
        using var session = store.OpenAsyncSession();
        var document = await session.LoadAsync<object>(id)
            ?? throw new InvalidOperationException($"'{id}' is not stored, so it has no etag.");
        return session.Advanced.GetChangeVectorFor(document);
    }

    /// <summary>
    /// The <c>items</c> of a <c>/po/delete-many</c> request (#467, D16): each id with its stored etag, or
    /// <see cref="ForMissingRow"/> for an id that is not stored.
    /// </summary>
    public static async Task<object[]> ItemsAsync(IDocumentStore store, IEnumerable<string> ids)
    {
        var requested = ids.ToArray();
        using var session = store.OpenAsyncSession();
        // One batched load: a 201-row request must not run into the session's 30-request limit.
        var documents = await session.LoadAsync<object>(requested.Distinct());
        return [.. requested.Select(id => (object)new
        {
            id,
            etag = documents.TryGetValue(id, out var document) && document is not null
                ? session.Advanced.GetChangeVectorFor(document)
                : ForMissingRow,
        })];
    }
}
