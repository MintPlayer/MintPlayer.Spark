using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// The framework-internal seam for resolving several ids in one pass, implemented by
/// <see cref="DefaultPersistentObjectActions{T}"/>.
/// </summary>
/// <remarks>
/// Internal and non-generic on purpose. Non-generic so <c>DatabaseAccess</c> — which only knows the
/// entity type as a <see cref="Type"/> — can use it without reflection. Internal because batching is
/// an optimization, not a hook: <c>IPersistentObjectActions&lt;T&gt;.OnLoadAsync</c> stays the one
/// load seam an actions class implements, and adding a plural sibling to it would be a second thing
/// to keep consistent for a case that has never come up in practice.
/// <para>
/// <see cref="SupportsBatchedLoad"/> is what keeps that honest: it is false as soon as a subclass
/// overrides <c>OnLoadAsync</c>, so a decorated page never gets skipped just because several rows
/// were requested at once.
/// </para>
/// </remarks>
internal interface IBatchedLoadActions
{
    /// <summary>Whether <see cref="LoadManyAsync"/> is equivalent to N calls to the load hook.</summary>
    bool SupportsBatchedLoad { get; }

    /// <summary>
    /// Resolves the given ids in one batched pass, in the order given. An id is omitted when it
    /// names no document, names a foreign collection, or is refused by the row rule — deliberately
    /// indistinguishable — so the result may be shorter than the request.
    /// </summary>
    Task<IReadOnlyList<PersistentObject>> LoadManyAsync(IReadOnlyList<string> ids, PersistentObject? parent);
}
