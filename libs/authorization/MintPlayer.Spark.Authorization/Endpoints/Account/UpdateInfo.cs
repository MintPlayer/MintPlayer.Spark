using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/manage/info</c>: change the email (mailed to the new address, opt-in through
/// <see cref="SparkEmailChange"/>) or the password, whenever accounts have passwords. Replaces
/// Microsoft's; see <see cref="SparkAccount"/>.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class UpdateInfo<TUser> : IPostEndpoint<InfoRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/info";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => LocalCredentialMode.HasPasswords(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SparkAccountMail<TUser> accountMail;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(InfoRequest request, CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext!;
        if (await userManager.GetUserAsync(context.User) is not { } user)
            return TypedResults.NotFound();

        var changesEmail = !string.IsNullOrEmpty(request.NewEmail)
            && !string.Equals(await userManager.GetEmailAsync(user), request.NewEmail, StringComparison.OrdinalIgnoreCase);

        // Refused before the password half runs, so a request carrying both changes nothing.
        if (changesEmail && !SparkAccount.EmailChangeEnabled(options.Value))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["EmailChangeDisabled"] = ["This application does not allow changing the email address."],
            });
        }

        if (!string.IsNullOrEmpty(request.NewEmail) && !SparkAccount.IsValidEmail(request.NewEmail))
            return SparkAccount.Problem(IdentityResult.Failed(userManager.ErrorDescriber.InvalidEmail(request.NewEmail)));

        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            if (string.IsNullOrEmpty(request.OldPassword))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["OldPasswordRequired"] = ["The old password is required to set a new password. If the old password is forgotten, use /resetPassword."],
                });
            }

            var changed = await userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
            if (!changed.Succeeded)
                return SparkAccount.Problem(changed);
        }

        if (changesEmail)
        {
            // Mailed to the NEW address; nothing changes until its link is followed.
            await accountMail.SendConfirmationAsync(context, user, request.NewEmail!, changedEmail: request.NewEmail);
        }

        return TypedResults.Ok(new InfoResponse
        {
            Email = await userManager.GetEmailAsync(user) ?? throw new NotSupportedException("Users must have an email."),
            IsEmailConfirmed = await userManager.IsEmailConfirmedAsync(user),
        });
    }
}
