using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.Authorization.Extensions;

public static class GitHubAuthenticationExtensions
{
    public static IdentityBuilder AddGitHub(
        this IdentityBuilder builder,
        Action<OAuthOptions> configureOptions)
    {
        return builder.AddGitHub("GitHub", configureOptions);
    }

    public static IdentityBuilder AddGitHub(
        this IdentityBuilder builder,
        string authenticationScheme,
        Action<OAuthOptions> configureOptions)
    {
        var authBuilder = new AuthenticationBuilder(builder.Services);
        authBuilder.AddOAuth(authenticationScheme, authenticationScheme, options =>
        {
            // GitHub OAuth defaults
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.CallbackPath = "/signin-github";

            // Map GitHub user info to standard claims
            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
            options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");

            // Allow consumer to override/extend
            configureOptions(options);
        });

        return builder;
    }
}
