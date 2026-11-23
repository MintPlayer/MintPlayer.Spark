namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// Who the current request is acting for: the signed-in user's id, and for an anonymous browser that asked
/// for something to be delivered later, its <b>visitor</b> id. Scoped — one answer per request.
/// </summary>
/// <remarks>
/// <para>
/// The seam audit fields (<c>CreatedBy</c>/<c>ModifiedBy</c>), moderation, notifications and similar
/// features read instead of reaching into <c>HttpContext.User</c> or cookies themselves (#460, #710 G4).
/// The default implementation reads the <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
/// claim of the request's principal, which is the user id for ASP.NET Core Identity, and the
/// Data-Protection-protected <c>spark.visitor</c> cookie; replace the registration to resolve it
/// differently — a background job acting for a user, a test.
/// </para>
/// <para>
/// ⚠️ An <b>id</b>, never a display name: an id stays valid when the user renames themselves and can
/// be resolved to "deleted user" once the account is gone, which a stored name cannot (GDPR).
/// </para>
/// <para>
/// <b>Visitors</b> (<c>docs/notification_bell_PRD.md</c> G4/G5). A visitor id identifies one anonymous
/// browser, never a person. It exists only after something called <see cref="EnsureVisitorId"/> — a plain
/// visit never gets the cookie — and it lives for <c>Spark:Visitor:Lifetime</c> (sliding). It is always
/// prefixed <c>visitor:</c>, so it can never equal a user id.
/// </para>
/// </remarks>
public interface ISparkCurrentPrincipal
{
    /// <summary>
    /// The current user's id, or <see langword="null"/> when <see cref="IsAuthenticated"/> is
    /// <see langword="false"/> — or when the principal is authenticated but carries no id claim
    /// (a module certificate, for instance).
    /// </summary>
    string? Id { get; }

    /// <summary>Whether the request's principal is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The anonymous browser's visitor id (<c>visitor:…</c>) when its cookie is present and valid, else
    /// <see langword="null"/>. Present for a signed-in caller too until the sign-in claims it; a feature
    /// addressing "the caller" should prefer <see cref="Id"/>. No request (a background job) has none.
    /// </summary>
    string? VisitorId => null;

    /// <summary>
    /// The visitor id of an <b>anonymous</b> caller, issuing (or renewing) the visitor cookie: call it when an
    /// anonymous request starts something that will be delivered later. Returns <see langword="null"/> for a
    /// signed-in caller (address the user instead) and outside a request.
    /// </summary>
    string? EnsureVisitorId() => null;

    /// <summary>
    /// Forgets the visitor: clears its cookies. Called once a sign-in claimed what was addressed to it.
    /// </summary>
    void ForgetVisitor() { }
}
