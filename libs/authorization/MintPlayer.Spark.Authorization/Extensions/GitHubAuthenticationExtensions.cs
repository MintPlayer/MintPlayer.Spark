using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        // #460 D7: GitHub's signal is reliable (email_verified from /user/emails, below) and absent
        // means "not verified or not obtainable" — refuse, as before. The user name is the GitHub
        // login verbatim: it is already a unique handle, and applications (CodeCoverage) compare it
        // with repository owners, which a slug would lower-case.
        builder.Services.AddSingleton(new MintPlayer.Spark.Authorization.Configuration.SparkExternalProviderRegistration(
            authenticationScheme,
            new MintPlayer.Spark.Authorization.Configuration.SparkExternalProviderPolicy
            {
                EmailVerification = MintPlayer.Spark.Authorization.Configuration.SparkExternalProviderPolicy.Default.EmailVerification,
                UserName = MintPlayer.Spark.Authorization.Configuration.SparkUserNameSource.ProviderHandle,
            }));

        var authBuilder = new AuthenticationBuilder(builder.Services);
        authBuilder.AddOAuth(authenticationScheme, authenticationScheme, options =>
        {
            // GitHub OAuth defaults
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.CallbackPath = "/signin-github";
            options.SignInScheme = IdentityConstants.ExternalScheme;
            // #490 D4: report a provider-side cancel or failure back to the popup or page; the
            // configure callback below may still replace it.
            options.Events.OnRemoteFailure = SparkExternalLoginRemoteFailure.Handle;

            // Required, not optional (#296). Auto-provisioning refuses to create an account without
            // an issuer-attested email, and the only source of that attestation is /user/emails,
            // which an OAuth App token cannot reach without this scope. Requesting nothing therefore
            // made first-time sign-in impossible — and in SparkLocalCredentials.Disabled, where there
            // are no local accounts to fall back on, made the app unsignable-into altogether.
            //
            // Inert for a GitHub App, which derives permissions from its installation and ignores
            // scopes entirely; there the equivalent is the "Email addresses: Read-only" account
            // permission. Added before configureOptions so a consumer can still override it.
            options.Scope.Add("user:email");

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
                // email_verified=true only when the primary entry is verified. The
                // Spark callback consumes that claim before auto-provisioning a new
                // TUser bound to the email.
                //
                // ⚠️ 4g: the *standard* claim name, not a `urn:github:` one. A per-provider term
                // would mean a new vocabulary entry for every forge, and a forge whose entry
                // nobody remembered to add would fail closed in a way that reads like a broken
                // provider rather than like missing code.
                //
                // Derived from the configured user endpoint rather than hard-coded, so a GitHub
                // Enterprise host (https://ghe.example/api/v3/user) asks its own server — a fixed
                // api.github.com would send the GHE token to public GitHub and never attest.
                using var emailsRequest = new HttpRequestMessage(HttpMethod.Get, EmailsEndpointFor(context.Options.UserInformationEndpoint));
                emailsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                emailsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                emailsRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("SparkAuth", "1.0"));

                using var emailsResponse = await context.Backchannel.SendAsync(emailsRequest, context.HttpContext.RequestAborted);
                if (!emailsResponse.IsSuccessStatusCode)
                {
                    // Loud, because the consequence is silent: no claim means the callback refuses to
                    // provision, and the user sees only a generic "email not verified". Without this
                    // line there is nothing anywhere connecting that to a missing scope or permission.
                    context.HttpContext.RequestServices
                        .GetService<ILoggerFactory>()
                        ?.CreateLogger(typeof(GitHubAuthenticationExtensions).FullName!)
                        .LogWarning(
                            "GitHub /user/emails returned {StatusCode}, so no verified-email claim was issued and " +
                            "a first-time sign-in cannot provision an account. For an OAuth App, grant the " +
                            "'user:email' scope; for a GitHub App, grant the 'Email addresses: Read-only' account " +
                            "permission. Signing in to an already-linked account is unaffected.",
                            (int)emailsResponse.StatusCode);
                }
                else
                {
                    // Shape-tolerant: an unexpected body means "not attested", not a failed sign-in.
                    using var emails = JsonDocument.Parse(await emailsResponse.Content.ReadAsStringAsync());
                    var entries = emails.RootElement.ValueKind == JsonValueKind.Array
                        ? emails.RootElement.EnumerateArray().ToArray()
                        : [];
                    foreach (var entry in entries)
                    {
                        if (entry.ValueKind == JsonValueKind.Object
                            && entry.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True
                            && entry.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True)
                        {
                            context.Identity?.AddClaim(new Claim("email_verified", "true"));
                            break;
                        }
                    }
                }
                // The claim is deliberately not emitted when the lookup fails — the callback then
                // refuses to auto-provision. Signing in to an *existing* linked account (matched by
                // ProviderKey) still works; only first-time binding is gated.
            };

            // Allow consumer to override/extend
            configureOptions(options);
        });

        return builder;
    }

    /// <summary><c>{user endpoint}/emails</c>: GitHub serves both from the same API root.</summary>
    private static Uri EmailsEndpointFor(string userInformationEndpoint)
    {
        var emails = new UriBuilder(userInformationEndpoint);
        emails.Path = emails.Path.TrimEnd('/') + "/emails";
        return emails.Uri;
    }
}
