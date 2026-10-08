using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>GET /spark/auth/confirmEmail</c>: a mailbox link, kept for links already sent by older versions
/// (new mails link to the SPA, which posts to <see cref="ConfirmEmail{TUser}"/>). Every mode.
/// </summary>
/// <remarks>
/// A plain top-level GET, no session: the single-use token is the credential (same reasoning as
/// <c>confirm-external-link</c>), so it carries no antiforgery stamp.
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ConfirmEmailLink<TUser> : IGetEndpoint<string>
    where TUser : SparkUser, new()
{
    public static string Path => "/confirmEmail";

    // Required (no initializer: an initializer would make an absent value keep it), so a link missing
    // either answers 400 as before rather than reaching the handler's 401.
    [QueryParam] public required string UserId { get; set; }
    [QueryParam] public required string Code { get; set; }
    [QueryParam] public string? ChangedEmail { get; set; }

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var result = await SparkAccount.ConfirmAsync(userManager, options.Value, UserId, Code, ChangedEmail);
        return result.Succeeded
            ? Results.Text("Thank you for confirming your email.")
            : Results.Unauthorized();
    }
}
