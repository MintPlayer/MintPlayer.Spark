namespace MintPlayer.Spark.Abstractions.Live;

/// <summary>
/// Tells live subscribers (<c>docs/guide-live-updates.md</c>) that data changed when nothing else can see it.
/// </summary>
/// <remarks>
/// <para>
/// Not needed for the common case. Every write through a RavenDB session of this process is seen by the
/// <c>Actions</c> source, and under the default <c>Spark:Live:Source = Both</c> every committed write from any
/// process is seen by the RavenDB Changes API. Call this for what neither sees: a patch or bulk insert under
/// <c>Source = Actions</c>, or a computed attribute or custom query that reads documents of another
/// collection than the one it is subscribed to.
/// </para>
/// <para>
/// A notification is only a dirty bit. The subscribers it touches re-run their read under each viewer's own
/// rights; a viewer who may not see the document gets no frame at all.
/// </para>
/// </remarks>
public interface ISparkLiveNotifier
{
    /// <summary>The document <paramref name="documentId"/> changed or was deleted.</summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="collection">Its collection, when known: live queries over that collection re-run too.</param>
    void Changed(string documentId, string? collection = null);

    /// <summary>Something in <paramref name="collection"/> changed: every live query over it re-runs.</summary>
    void CollectionChanged(string collection);
}
