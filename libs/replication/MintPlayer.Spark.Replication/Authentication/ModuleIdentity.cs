using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.Replication.Authentication;

/// <summary>
/// Puts the validated module onto the request, so the authorization pipeline has someone to
/// authorize.
/// <para>
/// The replication endpoints validate the caller themselves — <see cref="Services.IModuleCertificateValidator"/>
/// covers the certificate pin and the Development ladder — but validating is not the same as
/// authenticating: nothing was writing the result to <c>HttpContext.User</c>. So once M11 routed
/// cross-module writes through <c>IPermissionService</c>, those writes arrived <b>anonymous</b> and
/// were refused for holding only <c>Everyone</c>'s rights. The gate said "yes, this is HR" and the
/// permission check was never told.
/// </para>
/// <para>
/// The module name is safe to trust here <i>because</i> validation has already run: in Production it
/// was checked against the thumbprint pinned for that module, so the body cannot name a module whose
/// certificate it does not hold. In Development the name is all there is, which is the documented
/// trade of that mode.
/// </para>
/// </summary>
internal static class ModuleIdentity
{
    /// <summary>
    /// Marks the request as coming from <paramref name="moduleName"/>, carrying the same
    /// <c>Module:{Name}</c> group claim the certificate authentication scheme emits — so an operator
    /// writes one <c>security.json</c> entry regardless of which path established the identity.
    /// </summary>
    public static void EstablishModuleIdentity(this HttpContext context, string moduleName)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, moduleName),
                new Claim(ClaimTypes.Name, moduleName),
                new Claim("group", SparkModuleCertificateDefaults.GroupPrefix + moduleName),

                // A module is the system acting, not a viewer — row-level rules (which scope
                // rows to a person) don't apply to it. Type-level rights still do, via the
                // group claim above. See SparkSystemContext.
                new Claim(MintPlayer.Spark.Abstractions.Authentication.SparkSystemContext.ClaimType, "module"),
            ],
            SparkModuleCertificateDefaults.Scheme));
    }
}
