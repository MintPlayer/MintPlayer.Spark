using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// DI-shape tests for <see cref="GitHubAuthenticationExtensions.AddGitHub(IdentityBuilder, Action{OAuthOptions})"/>.
/// Pins GitHub OAuth defaults: registering a scheme misconfigured on these endpoints would
/// silently send users to wrong URLs. The OAuth backchannel + claim-actions logic itself
/// runs in <c>OnCreatingTicket</c> and needs an integration test to exercise the HttpClient
/// roundtrip; here we just pin the static configuration.
/// </summary>
public class GitHubAuthenticationExtensionsTests
{
    [Fact]
    public void AddGitHub_registers_authentication_scheme_with_default_name()
    {
        var services = new ServiceCollection();
        var identityBuilder = services.AddSparkAuthentication<SparkUser>();

        identityBuilder.AddGitHub(_ => { });

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = schemeProvider.GetSchemeAsync("GitHub").GetAwaiter().GetResult();

        scheme.Should().NotBeNull("the default scheme name is 'GitHub'");
        scheme!.DisplayName.Should().Be("GitHub");
    }

    [Fact]
    public void AddGitHub_with_custom_scheme_name_registers_under_that_name()
    {
        var services = new ServiceCollection();
        var identityBuilder = services.AddSparkAuthentication<SparkUser>();

        identityBuilder.AddGitHub("CustomGitHub", _ => { });

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = schemeProvider.GetSchemeAsync("CustomGitHub").GetAwaiter().GetResult();

        scheme.Should().NotBeNull();
    }

    [Fact]
    public void AddGitHub_pins_GitHub_OAuth_endpoints_and_callback_path()
    {
        var services = new ServiceCollection();
        var identityBuilder = services.AddSparkAuthentication<SparkUser>();

        identityBuilder.AddGitHub(options =>
        {
            options.ClientId = "test-client";
            options.ClientSecret = "test-secret";
        });

        using var provider = services.BuildServiceProvider();
        var oauthOptions = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("GitHub");

        oauthOptions.AuthorizationEndpoint.Should().Be("https://github.com/login/oauth/authorize");
        oauthOptions.TokenEndpoint.Should().Be("https://github.com/login/oauth/access_token");
        oauthOptions.UserInformationEndpoint.Should().Be("https://api.github.com/user");
        oauthOptions.CallbackPath.ToString().Should().Be("/signin-github");
        oauthOptions.SignInScheme.Should().Be(IdentityConstants.ExternalScheme);
    }

    [Fact]
    public void AddGitHub_lets_caller_override_defaults_after_built_in_configuration()
    {
        // The built-in setup runs first, then the user's callback — so user config wins.
        var services = new ServiceCollection();
        var identityBuilder = services.AddSparkAuthentication<SparkUser>();

        identityBuilder.AddGitHub(options =>
        {
            options.ClientId = "test-client";
            options.ClientSecret = "test-secret";
            options.CallbackPath = "/custom-github-callback";
        });

        using var provider = services.BuildServiceProvider();
        var oauthOptions = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("GitHub");

        oauthOptions.CallbackPath.ToString().Should().Be("/custom-github-callback");
    }

    [Fact]
    public void AddGitHub_returns_the_IdentityBuilder_for_chaining()
    {
        var services = new ServiceCollection();
        var identityBuilder = services.AddSparkAuthentication<SparkUser>();

        var returned = identityBuilder.AddGitHub(_ => { });

        returned.Should().BeSameAs(identityBuilder);
    }
}
