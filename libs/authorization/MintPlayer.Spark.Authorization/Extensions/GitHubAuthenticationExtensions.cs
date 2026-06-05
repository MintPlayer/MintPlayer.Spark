using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

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
            options.SignInScheme = IdentityConstants.ExternalScheme;

            // Map GitHub user info to standard claims
            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
            options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");

            // Fetch user info from GitHub API and apply claim mappings
            // (AddOAuth doesn't do this automatically — unlike AddGoogle/AddFacebook)
            options.Events.OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SparkAuth", "1.0"));

                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                context.RunClaimActions(user.RootElement);

                // R2-H11: GitHub's /user endpoint returns whatever email the user
                // set as primary, even if unverified. To attest the email we hit
                // /user/emails (requires the user:email scope) and emit
                // urn:github:email_verified=true only when the primary entry is
                // verified. The Spark callback consumes that claim before auto-
                // provisioning a new TUser bound to the email.
                using var emailsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
                emailsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                emailsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                emailsRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("SparkAuth", "1.0"));

                using var emailsResponse = await context.Backchannel.SendAsync(emailsRequest, context.HttpContext.RequestAborted);
                if (emailsResponse.IsSuccessStatusCode)
                {
                    using var emails = JsonDocument.Parse(await emailsResponse.Content.ReadAsStringAsync());
                    foreach (var entry in emails.RootElement.EnumerateArray())
                    {
                        if (entry.TryGetProperty("primary", out var primary) && primary.GetBoolean()
                            && entry.TryGetProperty("verified", out var verified) && verified.GetBoolean())
                        {
                            context.Identity?.AddClaim(new Claim("urn:github:email_verified", "true"));
                            break;
                        }
                    }
                }
                // If /user/emails fails (scope not granted, rate limit, network),
                // we deliberately don't emit the claim — the callback will refuse
                // to auto-provision. Logging in to an *existing* linked account
                // (matched by ProviderKey) still works; only first-time binding
                // is gated.
            };

            // Allow consumer to override/extend
            configureOptions(options);
        });

        return builder;
    }
}
