using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Passkeys;

/// <summary>
/// The signed-in user's enrolled passkeys, metadata only.
/// </summary>
/// <remarks>
/// <para>
/// Generic over <typeparamref name="TUser"/> because the ceremony runs through
/// <see cref="UserManager{TUser}"/>, which is registered closed over the application's user type.
/// An open generic is not mapped by this assembly's generated <c>MapSparkAuthEndpoints()</c>
/// (MPEP025, Info) — it is mapped explicitly by <c>MapSparkIdentityApi&lt;TUser&gt;</c>, where
/// <typeparamref name="TUser"/> is already concrete. That is what keeps the user type the
/// application's choice: closing it here, in the library, would pin every consumer to
/// <see cref="SparkUser"/> forever.
/// </para>
/// <para>
/// ⚠️ The constraint is restated on the class. It is <b>not</b> inherited from the method that maps
/// the endpoint, and <c>new()</c> is what <see cref="SparkUser"/>-derived construction needs
/// elsewhere in this surface.
/// </para>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ListPasskeys<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.RequireAuthorization();
    }

    [Inject] private readonly UserManager<TUser> userManager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
            return Results.Unauthorized();

        var passkeys = await userManager.GetPasskeysAsync(user);
        // Metadata only. The public key, attestation object and client data JSON never leave the
        // server: nothing in the UI needs them, and shipping them widens the blast radius of any
        // future leak on this route for no benefit.
        return Results.Ok(passkeys.Select(PasskeyEndpoints.ToSummary).ToArray());
    }
}
