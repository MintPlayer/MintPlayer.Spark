using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Passkeys;

/// <summary>
/// Starts a discoverable-credential sign-in ceremony.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ Anonymous, and takes no username — by design, and the design is load-bearing.
/// <c>MakePasskeyRequestOptionsAsync</c> accepts a user, and passing one populates
/// <c>allowCredentials</c>, which turns this route into a user-existence oracle: a known account
/// answers with credentials, an unknown one with an empty list. Passing <c>null</c> always means the
/// browser offers whatever discoverable credential it holds for this relying party, the assertion
/// carries the user handle, and the server resolves the account from the credential id afterwards.
/// The response is then identical for every caller.
/// </para>
/// <para>
/// No <c>Configure</c>: deliberately no authorization and no antiforgery stamp, because a caller who
/// is not signed in is exactly who this is for.
/// </para>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class PasskeyRequestOptions<TUser> : IPostEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys/request-options";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAuthFeatures.Passkeys(services);

    [Inject] private readonly SignInManager<TUser> signInManager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(null);
        return Results.Text(optionsJson, "application/json");
    }
}
