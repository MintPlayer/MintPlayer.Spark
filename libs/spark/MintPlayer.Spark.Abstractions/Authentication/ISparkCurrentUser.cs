namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// Who the current request is acting for, as far as stamping and auditing need to know: an id and
/// whether there is one. Scoped — one answer per request.
/// </summary>
/// <remarks>
/// <para>
/// The seam audit fields (<c>CreatedBy</c>/<c>ModifiedBy</c>), moderation and similar features read
/// instead of reaching into <c>HttpContext.User</c> themselves (#460). The default implementation
/// reads the <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> claim of the request's
/// principal, which is the user id for ASP.NET Core Identity; replace the registration to resolve it
/// differently — a background job acting for a user, a test.
/// </para>
/// <para>
/// ⚠️ An <b>id</b>, never a display name: an id stays valid when the user renames themselves and can
/// be resolved to "deleted user" once the account is gone, which a stored name cannot (GDPR).
/// </para>
/// </remarks>
public interface ISparkCurrentUser
{
    /// <summary>
    /// The current user's id, or <see langword="null"/> when <see cref="IsAuthenticated"/> is
    /// <see langword="false"/> — or when the principal is authenticated but carries no id claim
    /// (a module certificate, for instance).
    /// </summary>
    string? Id { get; }

    /// <summary>Whether the request's principal is authenticated.</summary>
    bool IsAuthenticated { get; }
}
