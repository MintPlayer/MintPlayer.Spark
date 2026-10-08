using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/manage/profile</c>: the user name, mail culture and contributed fields, saved all
/// or nothing. Every mode.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class UpdateProfile<TUser> : IPostEndpoint<SparkProfileRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/profile";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IUserStore<TUser> store;
    [Inject] private readonly IEnumerable<ISparkProfileContributor<TUser>> contributors;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(SparkProfileRequest request, CancellationToken cancellationToken)
    {
        if (await userManager.GetUserAsync(httpContextAccessor.HttpContext!.User) is not { } user)
            return Results.NotFound();

        var all = contributors.ToArray();
        var posted = request.Fields ?? new Dictionary<string, JsonElement>();
        var errors = new SparkProfileErrors();

        // Every posted field must belong to a contributor: an unknown one is a client bug or a probe,
        // and silently dropping it would report a save that did not happen.
        foreach (var name in posted.Keys)
        {
            if (!all.Any(c => c.Fields.Contains(name, StringComparer.OrdinalIgnoreCase)))
                errors.Add(name, "Unknown profile field.");
        }

        var perContributor = all
            .Select(c => (Contributor: c, Values: (IReadOnlyDictionary<string, JsonElement>)posted
                .Where(p => c.Fields.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)))
            .Where(x => x.Values.Count > 0)
            .ToArray();

        foreach (var (contributor, values) in perContributor)
            await contributor.ValidateAsync(user, values, errors, cancellationToken);

        // Absent keeps it; null or "" clears it (back to Spark:Mail:DefaultCulture).
        string? preferredCulture = null;
        if (request.HasPreferredCulture && !SparkAccount.TryNormalizeCulture(request.PreferredCulture, out preferredCulture))
            errors.Add("PreferredCulture", "Not a known culture name (for example 'en', 'nl-BE').");

        if (errors.HasErrors)
            return TypedResults.ValidationProblem(errors.ToDictionary());

        if (request.UserName is { } userName && !string.Equals(userName, user.UserName, StringComparison.Ordinal))
        {
            // Set on the entity; the single UpdateAsync below validates (uniqueness, characters, the '@'
            // rule), normalizes and saves it together with the contributed fields.
            await store.SetUserNameAsync(user, userName.Trim(), cancellationToken);
        }

        if (request.HasPreferredCulture)
            user.PreferredCulture = preferredCulture;

        foreach (var (contributor, values) in perContributor)
            await contributor.ApplyAsync(user, values, cancellationToken);

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return SparkAccount.Problem(result);

        return Results.Ok(await Profile<TUser>.ReadAsync(user, all, cancellationToken));
    }
}
