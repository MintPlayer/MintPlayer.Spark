namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Declares that an actions class owns row security for a type the framework cannot enforce for.
/// <para>
/// A type with no <c>clrType</c> is <em>composed</em>: its rows are computed per request rather than
/// stored, so there is no document for the framework to re-judge. Row filtering and redaction are
/// therefore skipped for it — deliberately, and correctly, since a row filter is an
/// <c>Expression&lt;Func&lt;TEntity,bool&gt;&gt;</c> over documents and these rows are not documents.
/// The type-level <c>Query</c> right still applies; nothing narrower does.
/// </para>
/// <para>
/// The problem that leaves is not the skipping — it is that <b>skipping deliberately and forgetting
/// entirely produced the same silence</b>. Both were "no <c>clrType</c>, so no enforcement", and no
/// reviewer, test or startup check could tell them apart. This interface separates them: a composed
/// type that serves rows must say so, and say where its scoping actually lives.
/// </para>
/// </summary>
/// <remarks>
/// This is a declaration, not a mechanism. Implementing it enforces nothing — it records that the
/// author knows the framework is not enforcing, and points the next reader at the code that is.
/// </remarks>
public interface ISparkOwnsRowSecurity
{
    /// <summary>
    /// How this type's rows are scoped to the caller, and <b>where that code lives</b>.
    /// <para>
    /// Name the file or service a reviewer must open. "The actions class handles it" is the answer
    /// this property exists to reject: in practice the scoping is often two layers down — a query
    /// method that delegates to a service which starts from the caller's own identity — and that
    /// service is the single line of defence, with no framework backstop behind it.
    /// </para>
    /// <para>
    /// Rendered at startup alongside the composed-query announcement, and required to be non-empty:
    /// an interface anyone can paste onto a class buys nothing, a sentence someone had to write does.
    /// </para>
    /// </summary>
    /// <example>
    /// "Rows are generated from IMyAccountsService.GetAsync, which starts from the caller's
    /// GitHub-verified installation owners (GitHubAccessService.GetVisibilityAsync). An anonymous
    /// caller yields an empty set, and every failure path narrows rather than widens."
    /// </example>
    string RowSecurityRationale { get; }
}
