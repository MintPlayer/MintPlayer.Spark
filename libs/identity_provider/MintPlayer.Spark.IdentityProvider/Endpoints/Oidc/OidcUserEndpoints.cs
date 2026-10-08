using System.Reflection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

/// <summary>
/// Maps the endpoints that are generic over the application's user type, closed once, at startup.
/// </summary>
/// <remarks>
/// <para>
/// The provider serves any user type an application registers with
/// <c>AddAuthentication&lt;TUser&gt;()</c>, but <c>AddIdentityProvider</c> is not generic: the type is
/// known only as <c>SparkModuleRegistry.IdentityUserType</c>. These endpoints used to resolve
/// <c>UserManager&lt;TUser&gt;</c>/<c>SignInManager&lt;TUser&gt;</c> from it by reflection on every
/// request. Instead they are generic, and <see cref="Map"/> closes them here with the one reflective
/// call left, so each endpoint injects its typed manager.
/// </para>
/// <para>
/// ⚠️ Open generics are left out of this assembly's generated <c>MapSparkIdentityProviderEndpoints()</c>
/// (MPEP025, Info): they are mapped only here. <see cref="EndpointRouteBuilderExtensions.MapEndpoint{TEndpoint}"/>
/// maps each under its <c>[MemberOf&lt;T&gt;]</c> group chain, honouring the group's <c>IsEnabled</c>
/// and <c>Configure</c>, so the routes and their conventions are what the generated mapping would give.
/// Mapped on the root builder, never on a group: the prefix comes from the membership, and mapping
/// onto a group as well would compose it twice.
/// </para>
/// </remarks>
internal static class OidcUserEndpoints
{
    /// <summary>Closes and maps every user-generic endpoint over <paramref name="userType"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="userType"/> is null: the provider was added without <c>AddAuthentication&lt;TUser&gt;()</c>.
    /// Refused at startup, because every one of these endpoints would otherwise fail on its first request.
    /// </exception>
    public static void Map(IEndpointRouteBuilder endpoints, Type? userType)
    {
        // ⚠️ No `?? typeof(SparkUser)` fallback, deliberately. Defaulting resolves
        // UserManager<SparkUser> from a container in which AddIdentityApiEndpoints<TUser> registered
        // UserManager<AppUser>, so an application with a derived user type would start and then fail
        // inside the token endpoint, where the cause is least visible.
        if (userType is null)
            throw new InvalidOperationException(
                "The identity provider needs the application's user type, and none is registered: "
                + "SparkModuleRegistry.IdentityUserType is null. Call AddAuthentication<TUser>() on the Spark builder.");

        typeof(OidcUserEndpoints)
            .GetMethod(nameof(MapFor), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(userType)
            .Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, [endpoints], culture: null);
    }

    private static void MapFor<TUser>(IEndpointRouteBuilder endpoints)
        where TUser : SparkUser, new()
    {
        endpoints.MapEndpoint<OidcTokenEndpoint<TUser>>();
        endpoints.MapEndpoint<OidcLoginSubmit<TUser>>();
    }
}
