using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/register</c>: self-registration, <see cref="SparkLocalCredentials.Full"/> only.
/// Replaces Microsoft's; see <see cref="SparkAccount"/>.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class Register<TUser> : IPostEndpoint<SparkRegisterRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/register";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => LocalCredentialMode.IsFull(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IUserStore<TUser> store;
    [Inject] private readonly SparkAccountMail<TUser> accountMail;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(SparkRegisterRequest registration, CancellationToken cancellationToken)
    {
        var email = registration.Email;
        var userName = registration.UserName?.Trim();

        if (!SparkAccount.IsValidEmail(email))
            return SparkAccount.Problem(IdentityResult.Failed(userManager.ErrorDescriber.InvalidEmail(email)));

        // G-Q22: the user name is the public handle, chosen here. It is never derived from the email
        // (Microsoft's handler used the email, which then showed on every label naming the user).
        if (string.IsNullOrEmpty(userName))
            return SparkAccount.Problem(IdentityResult.Failed(userManager.ErrorDescriber.InvalidUserName(userName)));

        // Through the store, not UserManager.SetUserNameAsync: the manager would save immediately.
        // CreateAsync below validates (uniqueness, characters, no '@') and saves once.
        var user = new TUser();
        await store.SetUserNameAsync(user, userName, CancellationToken.None);
        await ((IUserEmailStore<TUser>)store).SetEmailAsync(user, email, CancellationToken.None);

        var result = await userManager.CreateAsync(user, registration.Password ?? string.Empty);
        if (!result.Succeeded)
            return SparkAccount.Problem(result);

        await accountMail.SendConfirmationAsync(httpContextAccessor.HttpContext!, user, email!);
        return TypedResults.Ok();
    }
}
