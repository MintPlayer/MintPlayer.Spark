using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// Marks and detects principals that represent infrastructure rather than a person.
/// <para>
/// Row-level security scopes what a <em>viewer</em> may see and touch. Module-to-module sync
/// (authenticated via mTLS), replication, and background workers act on behalf of the system —
/// there is no viewer to scope rows to, and a user-shaped rule ("rows I created") evaluated
/// against a module principal would refuse legitimate infrastructure writes. Principal factories
/// for such identities stamp <see cref="ClaimType"/>; row security treats its presence — or the
/// absence of any HTTP request at all — as "no row rule applies". Entity-type authorization
/// (security.json's <c>Module:*</c> groups) still governs <em>which types</em> a module may touch.
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
    /// True when the current execution is the system acting, not a viewer: a system principal on
    /// the request, or no HTTP request at all (a background worker). A null
    /// <paramref name="accessor"/> means the caller isn't wired to the request pipeline (tests,
    /// manual construction) and is <b>not</b> treated as system — rules still apply.
    /// </summary>
    public static bool IsSystemContext(IHttpContextAccessor? accessor)
        => accessor is not null
            && (accessor.HttpContext is null || IsSystemPrincipal(accessor.HttpContext.User));
}
