using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
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
        return Results.Ok(new
        {
            account = new
            {
                id = user.Id,
                userName = user.UserName,
                email = user.Email,
                emailConfirmed = user.EmailConfirmed,
                phoneNumber = user.PhoneNumber,
                phoneNumberConfirmed = user.PhoneNumberConfirmed,
                twoFactorEnabled = user.TwoFactorEnabled,
                // Whether a password exists — never the hash. The deletion form asks for the password
                // only when there is one, and otherwise explains the recent-sign-in rule.
                hasPassword = await userManager.HasPasswordAsync(user),
                createdAtUtc = user.CreatedAtUtc,
                registrationMethod = user.RegistrationMethod,
                preferredCulture = user.PreferredCulture,
                roles = user.Roles,
                claims = user.Claims.Select(c => new { type = c.ClaimType, value = c.ClaimValue }),
                externalLogins = user.Logins.Select(l => new { provider = l.LoginProvider, displayName = l.ProviderDisplayName }),
                passkeys = user.Passkeys.Select(p => new { name = p.Name, createdAt = p.CreatedAt }),
            },
            data = contributions,
        });
    }
}
