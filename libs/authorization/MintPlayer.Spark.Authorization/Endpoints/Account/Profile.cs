using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary><c>GET /spark/auth/manage/profile</c>: the user name, email, mail culture and contributed fields. Every mode.</summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class Profile<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/profile";

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IEnumerable<ISparkProfileContributor<TUser>> contributors;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (await userManager.GetUserAsync(httpContext.User) is not { } user)
            return Results.NotFound();

        return Results.Ok(await ReadAsync(user, contributors, httpContext.RequestAborted));
    }

    /// <summary>The profile as both <c>GET</c> and <see cref="UpdateProfile{TUser}"/> answer it.</summary>
    internal static async Task<SparkProfileResponse> ReadAsync(
        TUser user, IEnumerable<ISparkProfileContributor<TUser>> contributors, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var contributor in contributors)
        {
            foreach (var (name, value) in await contributor.GetAsync(user, cancellationToken))
                fields[name] = value;
        }

        return new SparkProfileResponse(user.UserName, user.Email, fields) { PreferredCulture = user.PreferredCulture };
    }
}
