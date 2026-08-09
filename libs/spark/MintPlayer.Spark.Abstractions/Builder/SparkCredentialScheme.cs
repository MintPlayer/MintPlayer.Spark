namespace MintPlayer.Spark.Abstractions.Builder;

/// <summary>
/// One way of proving who is calling: an authentication scheme Spark's composite handler will
/// try, and whether the credential it reads is <i>ambient</i>.
/// </summary>
/// <param name="Name">The ASP.NET Core authentication scheme name.</param>
/// <param name="IsAmbient">
/// <c>true</c> when the browser attaches this credential to any request to the origin without the
/// caller doing anything — a cookie. That is the entire precondition for CSRF, and therefore the
/// only case where Spark demands an antiforgery token.
/// <para>
/// A bearer token, a client certificate or an API key is <c>false</c>: a cross-site page cannot
/// make the browser send one, so demanding an antiforgery token of such a caller protects nothing
/// and simply makes external POSTs impossible (F8).
/// </para>
/// </param>
public sealed record SparkCredentialScheme(string Name, bool IsAmbient);
