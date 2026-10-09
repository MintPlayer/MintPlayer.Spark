using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// <c>spark.AddExternalProviders(configuration)</c> (#490 D5, Q5): enables every provider that has
/// credentials under <c>Spark:Auth:Providers</c>.
/// </summary>
/// <remarks>
/// <code>
/// "Spark": { "Auth": { "Providers": {
///   "GitHub":           { "ClientId": "…", "ClientSecret": "…" },
///   "Google":           { "ClientId": "…", "ClientSecret": "…", "DisplayName": "Google" },
///   "MicrosoftAccount": { "ClientId": "…", "ClientSecret": "…" },
///   "Facebook":         { "ClientId": "…", "ClientSecret": "…" },
///   "Twitter":          { "ClientId": "…", "ClientSecret": "…" },
///   "LinkedIn":         { "ClientId": "…", "ClientSecret": "…" },
///   "OpenIdConnect": {
///     "MintPlayer": { "DisplayName": "MintPlayer ID", "Authority": "https://id.example", "ClientId": "…", "ClientSecret": "…" }
///   }
/// } } }
/// </code>
/// <list type="bullet">
/// <item><description>A provider with no section, or an empty <c>ClientId</c>, is not registered.</description></item>
/// <item><description>Any other key throws at startup, naming the valid ones — a misspelt provider would otherwise just never appear.</description></item>
/// <item><description>Per-provider code tuning: the <c>configure</c> hooks below (applied only to providers the configuration enabled), or a later individual call such as <c>spark.AddGitHub(o =&gt; …)</c>, which merges: configuration binds first, the later call's configure runs after it.</description></item>
/// </list>
/// </remarks>
public static class SparkExternalProvidersConfigurationExtensions
{
    /// <summary>The configuration section the providers are read from.</summary>
    public const string SectionPath = "Spark:Auth:Providers";

    internal const string OpenIdConnectKey = "OpenIdConnect";

    private static readonly string[] SocialKeys =
        ["GitHub", "Google", "MicrosoftAccount", "Facebook", "Twitter", "LinkedIn"];

    /// <summary>Enables every provider configured under <see cref="SectionPath"/>.</summary>
    /// <param name="spark">The Spark builder; <c>AddAuthentication&lt;TUser&gt;()</c> must already have run.</param>
    /// <param name="configuration">The application configuration (the section is read from its root).</param>
    /// <param name="configure">Optional per-provider tuning, run after the configuration is bound.</param>
    public static ISparkBuilder AddExternalProviders(
        this ISparkBuilder spark,
        IConfiguration configuration,
        Action<SparkExternalProviderHooks>? configure = null)
    {
        SparkExternalProviderExtensions.RequireAuthentication(spark, nameof(AddExternalProviders));
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionPath);
        var providers = section.GetChildren().ToArray();

        // Validated before anything is registered, so a typo fails the whole call rather than half of it.
        var unknown = providers
            .Select(provider => provider.Key)
            .Where(key => !SocialKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(key, OpenIdConnectKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"'{SectionPath}' contains unknown provider key(s): {string.Join(", ", unknown.Select(k => $"'{k}'"))}. "
                + $"Valid keys are {string.Join(", ", SocialKeys)} and {OpenIdConnectKey}:<scheme>. "
                + "Any other provider is registered in code and declared with spark.AddExternalScheme(scheme, policy).");
        }

        var hooks = new SparkExternalProviderHooks();
        configure?.Invoke(hooks);

        foreach (var provider in providers)
        {
            if (string.Equals(provider.Key, OpenIdConnectKey, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var oidc in provider.GetChildren())
                {
                    if (string.IsNullOrWhiteSpace(oidc["ClientId"]))
                        continue;

                    var scheme = oidc.Key;
                    var hook = hooks.OpenIdConnectHook(scheme);
                    spark.AddOpenIdConnect(scheme, NonEmpty(oidc["DisplayName"]) ?? scheme, options =>
                    {
                        options.Authority = NonEmpty(oidc["Authority"]);
                        options.ClientId = oidc["ClientId"];
                        options.ClientSecret = NonEmpty(oidc["ClientSecret"]);
                        hook?.Invoke(options);
                    });
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(provider["ClientId"]))
                continue;

            var displayName = NonEmpty(provider["DisplayName"]);
            switch (SocialKeys.First(key => string.Equals(key, provider.Key, StringComparison.OrdinalIgnoreCase)))
            {
                case "GitHub":
                    spark.AddGitHub(GitHubAuthenticationExtensions.GitHubScheme, displayName, Bind<OAuthOptions>(provider, hooks.GitHubHook));
                    break;
                case "Google":
                    spark.AddGoogle(GoogleDefaults.AuthenticationScheme, displayName, Bind<GoogleOptions>(provider, hooks.GoogleHook));
                    break;
                case "MicrosoftAccount":
                    spark.AddMicrosoftAccount(MicrosoftAccountDefaults.AuthenticationScheme, displayName, Bind<MicrosoftAccountOptions>(provider, hooks.MicrosoftAccountHook));
                    break;
                case "Facebook":
                    spark.AddFacebook(FacebookDefaults.AuthenticationScheme, displayName, Bind<FacebookOptions>(provider, hooks.FacebookHook));
                    break;
                case "Twitter":
                    spark.AddTwitter(SparkExternalProviderExtensions.TwitterScheme, displayName, Bind<OAuthOptions>(provider, hooks.TwitterHook));
                    break;
                case "LinkedIn":
                    spark.AddLinkedIn(SparkExternalProviderExtensions.LinkedInScheme, displayName, Bind<OAuthOptions>(provider, hooks.LinkedInHook));
                    break;
            }
        }

        return spark;
    }

    /// <summary>Only the two credentials are bound; the preset owns every other option.</summary>
    private static Action<TOptions> Bind<TOptions>(IConfigurationSection section, Action<TOptions>? hook)
        where TOptions : OAuthOptions
        => options =>
        {
            options.ClientId = section["ClientId"]!;
            options.ClientSecret = section["ClientSecret"] ?? string.Empty;
            hook?.Invoke(options);
        };

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Per-provider code tuning for <see cref="SparkExternalProvidersConfigurationExtensions.AddExternalProviders"/>.
/// Each hook runs after the configuration is bound, and only when the configuration enabled that provider.
/// </summary>
public sealed class SparkExternalProviderHooks
{
    private readonly Dictionary<string, Action<OpenIdConnectOptions>> openIdConnect = new(StringComparer.OrdinalIgnoreCase);

    internal Action<OAuthOptions>? GitHubHook { get; private set; }
    internal Action<GoogleOptions>? GoogleHook { get; private set; }
    internal Action<MicrosoftAccountOptions>? MicrosoftAccountHook { get; private set; }
    internal Action<FacebookOptions>? FacebookHook { get; private set; }
    internal Action<OAuthOptions>? TwitterHook { get; private set; }
    internal Action<OAuthOptions>? LinkedInHook { get; private set; }

    /// <summary>Tunes the GitHub provider.</summary>
    public SparkExternalProviderHooks GitHub(Action<OAuthOptions> configure) { GitHubHook += configure; return this; }

    /// <summary>Tunes the Google provider.</summary>
    public SparkExternalProviderHooks Google(Action<GoogleOptions> configure) { GoogleHook += configure; return this; }

    /// <summary>Tunes the Microsoft account provider.</summary>
    public SparkExternalProviderHooks MicrosoftAccount(Action<MicrosoftAccountOptions> configure) { MicrosoftAccountHook += configure; return this; }

    /// <summary>Tunes the Facebook provider.</summary>
    public SparkExternalProviderHooks Facebook(Action<FacebookOptions> configure) { FacebookHook += configure; return this; }

    /// <summary>Tunes the X (Twitter) provider.</summary>
    public SparkExternalProviderHooks Twitter(Action<OAuthOptions> configure) { TwitterHook += configure; return this; }

    /// <summary>Tunes the LinkedIn provider.</summary>
    public SparkExternalProviderHooks LinkedIn(Action<OAuthOptions> configure) { LinkedInHook += configure; return this; }

    /// <summary>Tunes the OpenID Connect provider configured under <c>OpenIdConnect:&lt;scheme&gt;</c>.</summary>
    public SparkExternalProviderHooks OpenIdConnect(string scheme, Action<OpenIdConnectOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        openIdConnect[scheme] = openIdConnect.TryGetValue(scheme, out var existing) ? existing + configure : configure;
        return this;
    }

    internal Action<OpenIdConnectOptions>? OpenIdConnectHook(string scheme)
        => openIdConnect.TryGetValue(scheme, out var hook) ? hook : null;
}
