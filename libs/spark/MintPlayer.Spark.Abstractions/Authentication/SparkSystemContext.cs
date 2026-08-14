using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// Marks and detects principals that represent infrastructure rather than a person.
/// <para>
/// Row-level security scopes what a <em>viewer</em> may see and touch. Module-to-module sync
/// (authenticated via mTLS) acts on behalf of the system — there is no viewer to scope rows to,
/// and a user-shaped rule ("rows I created") evaluated against a module principal would refuse
/// legitimate infrastructure writes. Principal factories for such identities stamp
/// <see cref="ClaimType"/>; row security exempts a request whose principal carries it. Entity-type
/// authorization (security.json's <c>Module:*</c> groups) still governs <em>which types</em> a
/// module may touch.
/// </para>
/// <para>
/// The exemption is <b>positive-claim-only</b>, deliberately. The absence of an HTTP request is
/// <em>not</em> system context — that is the default state of every non-request code path (tests,
/// manual construction, unconfigured pipelines), and treating it as exempt would silently switch
/// row security off wherever there is no live request. Row security fails closed: if you cannot
/// prove the caller is the system, the caller is a viewer and the rules apply. A background job
/// that genuinely needs the exemption must run under a principal carrying <see cref="ClaimType"/>.
/// </para>
/// </summary>
public static class SparkSystemContext
{
    /// <summary>Claim stamped on system principals (modules, replication). Value is a short
    /// origin label ("module"), informational only — presence is what matters.</summary>
    public const string ClaimType = "spark:system-context";

    public static bool IsSystemPrincipal(ClaimsPrincipal? principal)
        => principal?.HasClaim(c => string.Equals(c.Type, ClaimType, StringComparison.Ordinal)) == true;

    /// <summary>
    /// True only when the current request's principal explicitly carries the system claim. A null
    /// accessor, a null <see cref="IHttpContextAccessor.HttpContext"/>, or an ordinary user
    /// principal all mean "not system" — rules apply (fail closed).
    /// </summary>
    public static bool IsSystemContext(IHttpContextAccessor? accessor)
        => IsSystemPrincipal(accessor?.HttpContext?.User);
}
