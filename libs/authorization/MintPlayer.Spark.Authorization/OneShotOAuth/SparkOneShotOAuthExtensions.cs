using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Authorization.OneShotOAuth;

/// <summary>Registers one-shot OAuth clients (#554 f1).</summary>
public static class SparkOneShotOAuthExtensions
{
    /// <summary>
    /// Registers an OAuth client that custom actions borrow a token from through
    /// <see cref="ISparkOneShotOAuth"/>, and maps <c>GET /spark/auth/oauth-callback</c>.
    /// </summary>
    /// <remarks>
    /// The client's registered callback URL must be <c>{origin}/spark/auth/oauth-callback</c> (or a
    /// parent of it, where the provider allows that). Needs <c>spark.AddAuthentication&lt;TUser&gt;()</c>
    /// first: the token is bound to the signed-in user. The browser side is <c>withAuth()</c> from
    /// <c>@mintplayer/ng-spark/auth</c>, which registers the <c>oauth.authorize</c> client method.
    /// </remarks>
    /// <example>
    /// <code>
    /// spark.AddOneShotOAuthClient("rulesets", client =&gt;
    /// {
    ///     client.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
    ///     client.TokenEndpoint = "https://github.com/login/oauth/access_token";
    ///     client.ClientId = configuration["RulesetsOAuth:ClientId"]!;
    ///     client.ClientSecret = configuration["RulesetsOAuth:ClientSecret"];
    ///     client.Revoke = (revocation, ct) =&gt; ...;
    /// });
    /// </code>
    /// </example>
    public static ISparkBuilder AddOneShotOAuthClient(this ISparkBuilder spark, string name, Action<SparkOneShotOAuthClient> configure)
    {
        SparkExternalProviderExtensions.RequireAuthentication(spark, nameof(AddOneShotOAuthClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        spark.Services.Configure<SparkOneShotOAuthOptions>(options =>
        {
            if (!options.Clients.TryGetValue(name, out var client))
                options.Clients[name] = client = new SparkOneShotOAuthClient();
            configure(client);
        });

        spark.Services.AddHttpClient();
        spark.Services.AddDataProtection();
        spark.Services.TryAddSingleton(TimeProvider.System);
        spark.Services.TryAddSingleton<SparkOneShotOAuthStore>();
        spark.Services.TryAddScoped<ISparkOneShotOAuth, SparkOneShotOAuth>();
        spark.Services.TryAddScoped<SparkOneShotOAuthCallback>();
        return spark;
    }
}
