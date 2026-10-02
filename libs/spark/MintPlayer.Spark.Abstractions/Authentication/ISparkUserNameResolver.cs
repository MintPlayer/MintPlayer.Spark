namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// Resolves user ids (the ids <see cref="ISparkCurrentUser"/> gives, which features store) to display
/// names, at read time — names are never copied into documents (GDPR: a renamed or deleted account
/// shows its current name, or none).
/// </summary>
/// <remarks>
/// <para>
/// Optional: a feature that shows names resolves <c>ISparkUserNameResolver?</c> and shows no name
/// without one. Neither side references the other — the app (or a library) registers one, and any
/// feature uses it. History registers its <c>IHistoryUserNameResolver</c> here too
/// (<c>AddHistoryUserNameResolver&lt;T&gt;()</c>), unless the app registered one of its own.
/// Contributions uses it for <c>ContributorName</c> (contributions M5b).
/// </para>
/// <para>⚠️ Never return the id itself as a name: an id missing from the result means "no name".</para>
/// </remarks>
public interface ISparkUserNameResolver
{
    /// <summary>A name per id it knows, for every distinct id asked for in one call (batched).</summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);
}
