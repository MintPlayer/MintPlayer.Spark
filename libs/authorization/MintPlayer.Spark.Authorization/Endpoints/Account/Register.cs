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
/// <remarks>
/// <para>
/// A13: an address that already has an account gets the answer a fresh one gets (200, no body) after
/// the same password hash, one token and one queued mail, and the owner is mailed
/// <see cref="SparkAuthMailTemplates.AccountExists"/>; nothing about the account changes. Microsoft's
/// handler answered <c>DuplicateEmail</c>, which told anyone that the address was registered.
/// </para>
/// <para>
/// This endpoint never signs in, with or without <see cref="SparkAuthenticationOptions.RequireConfirmedEmail"/>,
/// so the same answer holds in both modes. What it cannot hide is the account it created: the registrant
/// knows the new password, and a sign-in with it succeeds (or, unconfirmed, answers <c>NotAllowed</c>)
/// for a fresh address and fails for a taken one. That oracle is the sign-in endpoint's, it costs the
/// prober a real account per probe, and the user name (a public handle, G-Q22) still answers
/// <c>DuplicateUserName</c> by design.
/// </para>
/// </remarks>
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

        // The password is validated and hashed before the user is (UserManager.CreateAsync(user, password)),
        // so a taken address has paid for the hash by the time its DuplicateEmail comes back.
        var result = await userManager.CreateAsync(user, registration.Password ?? string.Empty);
        var context = httpContextAccessor.HttpContext!;
        if (!result.Succeeded)
        {
            // A13: DuplicateEmail said that the address has an account. A taken address answers as a fresh
            // one does, and its owner is told instead. Any other error (password rules, a taken user
            // name) is the registrant's to fix and is answered as before.
            // (With IdentityOptions.User.RequireUniqueEmail, Identity's validator reports it beside them.)
            if (!result.Errors.Any(e => e.Code == DuplicateEmail))
                return SparkAccount.Problem(result);
            var others = result.Errors.Where(e => e.Code != DuplicateEmail).ToArray();
            if (others.Length > 0)
                return SparkAccount.Problem(IdentityResult.Failed(others));

            // To the address the account holds, not the spelling the registrant typed.
            if (await userManager.FindByEmailAsync(email!) is { } owner)
                await accountMail.QueueAccountExistsAsync(context, owner, owner.Email ?? email!);
            else
                // The owner left between the two calls: the same token work, and nothing is sent.
                await accountMail.QueueConfirmationAsync(context, null, email!);
            return TypedResults.Ok();
        }

        // Queued, not awaited, like the taken address's notice: neither answer waits for a mail.
        await accountMail.QueueConfirmationAsync(context, user, email!);
        return TypedResults.Ok();
    }

    private static readonly string DuplicateEmail = nameof(IdentityErrorDescriber.DuplicateEmail);
}
