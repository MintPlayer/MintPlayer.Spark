namespace MintPlayer.Spark.Abstractions.Visitor;

/// <summary>
/// Who is asking, for state that must survive a refresh before anyone signs in: a guest's basket, a place in
/// a waiting room (commerce PRD X1, <c>docs/guide-controllers.md</c> "Visitors").
/// </summary>
/// <remarks>
/// <para>
/// A request feature (<c>HttpContext.Features.Get&lt;ISparkVisitor&gt;()</c>, or injected), <b>never</b> a
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> identity: a guest with a visitor cookie is still
/// anonymous to every authorization rule, the <c>[SparkAuthorize]</c> default and the live socket's ticket
/// expiry.
/// </para>
/// <para>
/// The cookie is a strictly necessary one, issued only by an action (<see cref="EnsureAsync"/>), never on a
/// read. Its value is protected by Spark's Data Protection key ring, so a forged or tampered cookie is
/// treated as absent. A request carrying a valid one is held to antiforgery on its anonymous mutating calls,
/// exactly like a signed-in one: the cookie is ambient authority over the guest's basket.
/// </para>
/// </remarks>
public interface ISparkVisitor
{
    /// <summary>The visitor id from a cookie that unprotected successfully; <see langword="null"/> otherwise.</summary>
    string? VisitorId { get; }

    /// <summary>The signed-in user's id (<c>ClaimTypes.NameIdentifier</c>), if the request is authenticated.</summary>
    string? UserId { get; }

    /// <summary>
    /// The key per-identity limits are kept under, most specific first: <c>user:{id}</c>, then
    /// <c>visitor:{id}</c>, then <c>ip:{address}</c> with an IPv6 address grouped to its /64
    /// (<see cref="SparkClientAddress.Key"/>).
    /// </summary>
    string IdentityKey { get; }

    /// <summary>Whether the request carries a valid visitor cookie (<see cref="VisitorId"/> is set).</summary>
    bool HasVisitorCookie { get; }

    /// <summary>
    /// The visitor id, issuing the cookie first when the request has none. Call it from the action that needs
    /// a guest identity (adding to a basket, joining a queue).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The request is a read (<c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c>): a strictly necessary cookie is set by an
    /// action, never by visiting a page. Or the response has already started.
    /// </exception>
    ValueTask<string> EnsureAsync();
}

/// <summary>
/// Runs the <c>ISparkVisitorMergeHandler</c>s (Spark core) for the current request's visitor, once, and forgets the
/// visitor cookie. Implemented by Spark core; Authorization calls it from its sign-in hook.
/// </summary>
public interface ISparkVisitorMerge
{
    /// <summary>
    /// Merges the current request's visitor (if it carries a valid visitor cookie) into
    /// <paramref name="userId"/>, then deletes the cookie. A no-op without a visitor cookie.
    /// </summary>
    /// <returns><see langword="true"/> when this call ran the handlers; <see langword="false"/> when there was no
    /// visitor, or the merge had already happened.</returns>
    Task<bool> MergeIntoAsync(string userId, CancellationToken cancellationToken);
}
