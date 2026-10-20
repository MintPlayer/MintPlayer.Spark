using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Contracts;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>GET /spark/auth/manage/personal-data</c>: what the account holds about the person, plus every
/// <see cref="ISparkPersonalDataContributor{TUser}"/>'s data, as a download (D8). Every mode.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class PersonalData<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/personal-data";

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IEnumerable<ISparkPersonalDataContributor<TUser>> contributors;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (await userManager.GetUserAsync(httpContext.User) is not { } user)
            return Results.NotFound();

        var contributions = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var contributor in contributors)
        {
            if (await contributor.GetPersonalDataAsync(user, httpContext.RequestAborted) is { } data)
                contributions[contributor.Name] = data;
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.ContentDisposition = "attachment; filename=\"personal-data.json\"";

        // What the account holds about the person — never credentials, hashes, keys or tokens.
        // Whether a password exists — never the hash. The deletion form asks for the password only when
        // there is one, and otherwise explains the recent-sign-in rule.
        return Results.Ok(Describe(user, await userManager.HasPasswordAsync(user), contributions));
    }

    /// <summary>The response for <paramref name="user"/> (the shape test's seam).</summary>
    internal static SparkPersonalData Describe(TUser user, bool hasPassword, IReadOnlyDictionary<string, object?> contributions)
        => new(
            new SparkPersonalDataAccount(
                user.Id,
                user.UserName,
                user.Email,
                user.EmailConfirmed,
                user.PhoneNumber,
                user.PhoneNumberConfirmed,
                user.TwoFactorEnabled,
                hasPassword,
                user.CreatedAtUtc,
                user.RegistrationMethod,
                user.PreferredCulture,
                user.PreferredTimeZone,
                user.Roles,
                [.. user.Claims.Select(c => new SparkPersonalDataClaim(c.ClaimType, c.ClaimValue))],
                [.. user.Logins.Select(l => new SparkPersonalDataExternalLogin(l.LoginProvider, l.ProviderDisplayName))],
                [.. user.Passkeys.Select(p => new SparkPersonalDataPasskey(p.Name, p.CreatedAt))]),
            contributions);
}
