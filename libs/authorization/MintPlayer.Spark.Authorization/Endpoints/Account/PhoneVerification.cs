using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>GET /spark/auth/manage/phone</c>: the signed-in user's verified number and the number a code is pending
/// for (<c>docs/commerce_PRD.md</c> X13). Mapped only with <c>spark.AddPhoneVerification()</c>.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class GetPhoneVerification : IGetEndpoint
{
    public static string Path => "/phone";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkPhoneVerificationHttp.IsEnabled(services);

    [Inject] private readonly ISparkPhoneVerification verification;
    [Inject] private readonly IOptions<IdentityOptions> identityOptions;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (SparkPhoneVerificationHttp.UserId(httpContext, identityOptions.Value) is not { } userId)
            return Results.NotFound();

        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await verification.GetStatusAsync(userId, httpContext.RequestAborted));
    }
}

/// <summary>
/// <c>POST /spark/auth/manage/phone/send</c> <c>{ phoneNumber }</c>: sends a code by SMS. 200 when sent, 400 for a
/// number that is not one or not allowed, 429 with <c>Retry-After</c> at a send limit, 502 when the SMS vendor refused.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class SendPhoneVerificationCode : IPostEndpoint<SparkPhoneSendRequest>
{
    public static string Path => "/phone/send";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkPhoneVerificationHttp.IsEnabled(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkPhoneVerification verification;
    [Inject] private readonly IOptions<IdentityOptions> identityOptions;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(SparkPhoneSendRequest request, CancellationToken cancellationToken)
    {
        if (SparkPhoneVerificationHttp.UserId(httpContextAccessor.HttpContext!, identityOptions.Value) is not { } userId)
            return Results.NotFound();

        var result = await verification.SendCodeAsync(userId, request.PhoneNumber ?? string.Empty, cancellationToken);
        return SparkPhoneVerificationHttp.ToHttp(httpContextAccessor.HttpContext!, result);
    }
}

/// <summary>
/// <c>POST /spark/auth/manage/phone/verify</c> <c>{ code }</c>: checks the pending code. 200 when the number is now
/// verified, 400 for a wrong, expired, voided or missing code or a number another account verified, 429 when
/// parallel guesses collided.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class VerifyPhoneNumber : IPostEndpoint<SparkPhoneVerifyRequest>
{
    public static string Path => "/phone/verify";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkPhoneVerificationHttp.IsEnabled(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkPhoneVerification verification;
    [Inject] private readonly IOptions<IdentityOptions> identityOptions;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(SparkPhoneVerifyRequest request, CancellationToken cancellationToken)
    {
        if (SparkPhoneVerificationHttp.UserId(httpContextAccessor.HttpContext!, identityOptions.Value) is not { } userId)
            return Results.NotFound();

        var result = await verification.VerifyAsync(userId, request.Code ?? string.Empty, cancellationToken);
        return SparkPhoneVerificationHttp.ToHttp(httpContextAccessor.HttpContext!, result);
    }
}

/// <summary>The body of <c>POST /spark/auth/manage/phone/send</c>.</summary>
public sealed class SparkPhoneSendRequest
{
    /// <summary>The number, international (<c>+32 470 12 34 56</c>) or national when a default country is configured.</summary>
    public string? PhoneNumber { get; set; }
}

/// <summary>The body of <c>POST /spark/auth/manage/phone/verify</c>.</summary>
public sealed class SparkPhoneVerifyRequest
{
    /// <summary>The code from the text message.</summary>
    public string? Code { get; set; }
}

/// <summary>What the phone-verification endpoints share: whether they exist, and the result's HTTP answer.</summary>
internal static class SparkPhoneVerificationHttp
{
    internal static bool IsEnabled(IServiceProvider root)
        => root.GetService<IServiceProviderIsService>()?.IsService(typeof(SparkPhoneVerificationExtensions.PhoneVerificationMarker)) == true;

    /// <summary>The signed-in user's id, read the way Identity reads it (<see cref="ClaimsIdentityOptions.UserIdClaimType"/>).</summary>
    internal static string? UserId(HttpContext context, IdentityOptions options)
        => context.User.FindFirst(options.ClaimsIdentity.UserIdClaimType)?.Value is { Length: > 0 } id ? id : null;

    internal static IResult ToHttp(HttpContext context, SparkPhoneVerificationResult result)
    {
        context.Response.Headers.CacheControl = "no-store";
        var outcome = JsonName(result.Status);
        switch (result.Status)
        {
            case SparkPhoneVerificationStatus.CodeSent:
            case SparkPhoneVerificationStatus.Verified:
            case SparkPhoneVerificationStatus.AlreadyVerified:
                return Results.Ok(new { outcome, phoneNumber = result.PhoneNumber, expiresAtUtc = result.ExpiresAtUtc });

            case SparkPhoneVerificationStatus.RateLimited:
                var seconds = (int)Math.Ceiling((result.RetryAfter ?? TimeSpan.Zero).TotalSeconds);
                context.Response.Headers.RetryAfter = Math.Max(seconds, 1).ToString(CultureInfo.InvariantCulture);
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many codes requested. Try again later.", extensions: new Dictionary<string, object?> { ["outcome"] = outcome });

            case SparkPhoneVerificationStatus.NotSent:
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The text message could not be sent. Try again later.", extensions: new Dictionary<string, object?> { ["outcome"] = outcome });

            case SparkPhoneVerificationStatus.UnknownUser:
                return Results.NotFound();

            case SparkPhoneVerificationStatus.InvalidNumber:
                return Invalid("phoneNumber", "Not a phone number. Enter it with the country code, for example +32 470 12 34 56.", outcome);
            case SparkPhoneVerificationStatus.CountryNotAllowed:
                return Invalid("phoneNumber", "Numbers in this country cannot be verified here.", outcome);
            case SparkPhoneVerificationStatus.NumberInUse:
                return Invalid("phoneNumber", "This number already verifies another account.", outcome);
            case SparkPhoneVerificationStatus.WrongCode:
                return Invalid("code", "That code is not right.", outcome, result.AttemptsLeft);
            case SparkPhoneVerificationStatus.Expired:
                return Invalid("code", "That code has expired. Request a new one.", outcome);
            case SparkPhoneVerificationStatus.TooManyAttempts:
                return Invalid("code", "Too many wrong codes. Request a new one.", outcome);
            default:
                return Invalid("code", "No code is pending. Request one first.", outcome);
        }
    }

    private static IResult Invalid(string field, string message, string outcome, int? attemptsLeft = null)
    {
        var extensions = new Dictionary<string, object?> { ["outcome"] = outcome };
        if (attemptsLeft is { } left)
            extensions["attemptsLeft"] = left;
        return Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, extensions: extensions);
    }

    /// <summary><c>CodeSent</c> → <c>codeSent</c>: the client switches on this.</summary>
    private static string JsonName(SparkPhoneVerificationStatus status)
    {
        var name = status.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
