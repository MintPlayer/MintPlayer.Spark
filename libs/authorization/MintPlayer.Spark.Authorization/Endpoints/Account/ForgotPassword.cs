using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/forgotPassword</c>, whenever accounts have passwords. Replaces Microsoft's; see
/// <see cref="SparkAccount"/>.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ForgotPassword<TUser> : IPostEndpoint<ForgotPasswordRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/forgotPassword";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => LocalCredentialMode.HasPasswords(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SparkAccountMail<TUser> accountMail;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        // D6: unconfirmed addresses too. Microsoft sends only to confirmed ones, which strands every
        // account created before confirmation was enforced; completing the reset confirms the email.
        var user = await userManager.FindByEmailAsync(request.Email);

        // A11: the same token work for an unknown address, and the mail is queued, not awaited, so
        // neither the answer nor its timing says whether the address has an account.
        await accountMail.QueuePasswordResetAsync(httpContextAccessor.HttpContext!, user, request.Email);

        return TypedResults.Ok();
    }
}
