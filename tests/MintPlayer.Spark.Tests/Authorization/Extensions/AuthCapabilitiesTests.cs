using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization;
using MintPlayer.Spark.Authorization.Configuration;
using Account = MintPlayer.Spark.Authorization.Endpoints.Account;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Pins <c>GET /spark/auth/capabilities</c> — the channel that stops the server's auth
/// configuration and the client's from silently disagreeing.
/// </summary>
public class AuthCapabilitiesTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private async Task<IHost> StartAsync(
        SparkLocalCredentials mode,
        SparkEmailChange emailChange = SparkEmailChange.Disabled,
        SparkExternalLoginLinking linking = SparkExternalLoginLinking.Disabled,
        SparkSignInIdentifiers? identifiers = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.Configure<SparkAuthenticationOptions>(o =>
                    {
                        o.LocalCredentials = mode;
                        o.EmailChange = emailChange;
                        o.ExternalLoginLinking = linking;
                        if (identifiers is { } value)
                            o.SignInIdentifiers = value;
                    });
                    services.AddTestMailSink(); // #460 D6: registration needs a mail sender

                    // Two providers a human can click, plus one machine-only scheme that must not
                    // be offered as a sign-in button.
                    services.AddAuthentication()
                        .AddCookie("GitHub", "GitHub", _ => { })
                        .AddCookie("Google", "Google", _ => { })
                        .AddCookie("ApiToken", displayName: null, _ => { });

                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>());
                }))
            .StartAsync();
    }

    private static async Task<JsonElement> GetCapabilitiesAsync(IHost host)
    {
        using var client = host.GetTestServer().CreateClient();
        var response = await client.GetAsync("/spark/auth/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData(SparkLocalCredentials.Full, "Full")]
    [InlineData(SparkLocalCredentials.SignInOnly, "SignInOnly")]
    [InlineData(SparkLocalCredentials.Disabled, "Disabled")]
    public async Task Capabilities_reports_the_configured_mode(SparkLocalCredentials mode, string expected)
    {
        using var host = await StartAsync(mode);

        var body = await GetCapabilitiesAsync(host);

        body.GetProperty("localCredentials").GetString().Should().Be(expected);
    }

    [Theory]
    [InlineData(SparkLocalCredentials.Full, null, "email,userName")] // the default: either
    [InlineData(SparkLocalCredentials.Full, SparkSignInIdentifiers.Email, "email")]
    [InlineData(SparkLocalCredentials.SignInOnly, SparkSignInIdentifiers.UserName, "userName")]
    [InlineData(SparkLocalCredentials.Disabled, null, "")] // no password sign-in, nothing to type
    public async Task Capabilities_reports_the_sign_in_identifiers(
        SparkLocalCredentials mode, SparkSignInIdentifiers? identifiers, string expected)
    {
        using var host = await StartAsync(mode, identifiers: identifiers);

        var body = await GetCapabilitiesAsync(host);

        string.Join(",", body.GetProperty("signInIdentifiers").EnumerateArray().Select(e => e.GetString()))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(SparkLocalCredentials.Full, SparkEmailChange.Disabled, false)] // the default: opt-in only
    [InlineData(SparkLocalCredentials.Full, SparkEmailChange.Enabled, true)]
    [InlineData(SparkLocalCredentials.SignInOnly, SparkEmailChange.Enabled, true)]
    [InlineData(SparkLocalCredentials.Disabled, SparkEmailChange.Enabled, false)] // POST manage/info is not mapped
    public async Task Capabilities_reports_email_change_only_when_opted_in_and_mapped(
        SparkLocalCredentials mode, SparkEmailChange emailChange, bool expected)
    {
        using var host = await StartAsync(mode, emailChange);

        var body = await GetCapabilitiesAsync(host);

        body.GetProperty("emailChange").GetBoolean().Should().Be(expected);
    }

    // External SIGN-IN (the GitHub/Google schemes registered above) is on in every case: the
    // connected-logins page depends on LINKING, which is separate — CodeCoverage signs in with GitHub
    // but links nothing, and its account page offered a link to a page that was not there.
    [Theory]
    [InlineData(SparkExternalLoginLinking.Disabled, false)]
    [InlineData(SparkExternalLoginLinking.WhenSignedIn, true)]
    public async Task Capabilities_reports_external_logins_only_when_linking_maps_the_page(
        SparkExternalLoginLinking linking, bool expected)
    {
        using var host = await StartAsync(SparkLocalCredentials.Full, linking: linking);

        var body = await GetCapabilitiesAsync(host);

        body.GetProperty("externalLogins").GetBoolean().Should().Be(expected);
    }

    [Fact]
    public async Task Capabilities_reports_the_registered_external_providers()
    {
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var body = await GetCapabilitiesAsync(host);
        var schemes = body.GetProperty("externalProviders")
            .EnumerateArray()
            .Select(provider => provider.GetProperty("scheme").GetString())
            .ToArray();

        schemes.Should().BeEquivalentTo(["GitHub", "Google"]);
    }

    [Fact]
    public async Task Capabilities_omits_non_interactive_schemes()
    {
        // The scheme table mixes interactive providers with machine-caller credential schemes and
        // Identity's own internal cookies. Only the first belongs on a sign-in page — and offering
        // a bearer or certificate scheme as a button would be a dead end for the user.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var body = await GetCapabilitiesAsync(host);
        var schemes = body.GetProperty("externalProviders")
            .EnumerateArray()
            .Select(provider => provider.GetProperty("scheme").GetString())
            .ToArray();

        schemes.Should().NotContain("ApiToken");
        schemes.Should().NotContain(IdentityConstants.ApplicationScheme);
        schemes.Should().NotContain(IdentityConstants.ExternalScheme);
        schemes.Should().NotContain(IdentityConstants.BearerScheme);
    }

    [Fact]
    public async Task Capabilities_lists_the_providers_enabled_from_configuration_with_their_display_names()
    {
        // #490 M4: Spark:Auth:Providers drives the buttons. Google has no ClientId, so it is absent.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spark:Auth:Providers:GitHub:ClientId"] = "gh-id",
                ["Spark:Auth:Providers:GitHub:ClientSecret"] = "gh-secret",
                ["Spark:Auth:Providers:GitHub:DisplayName"] = "GitHub Enterprise",
                ["Spark:Auth:Providers:Google:ClientId"] = "",
                ["Spark:Auth:Providers:Twitter:ClientId"] = "x-id",
                ["Spark:Auth:Providers:Twitter:ClientSecret"] = "x-secret",
                ["Spark:Auth:Providers:OpenIdConnect:MintPlayer:DisplayName"] = "MintPlayer ID",
                ["Spark:Auth:Providers:OpenIdConnect:MintPlayer:Authority"] = "https://idp.test",
                ["Spark:Auth:Providers:OpenIdConnect:MintPlayer:ClientId"] = "rp",
            })
            .Build();

        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.Configure<SparkAuthenticationOptions>(o => o.LocalCredentials = SparkLocalCredentials.Disabled);
                    services.AddTestMailSink();
                    TestSparkAuth.Builder(services, configuration).AddExternalProviders(configuration);
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>());
                }))
            .StartAsync();

        var body = await GetCapabilitiesAsync(host);
        var providers = body.GetProperty("externalProviders")
            .EnumerateArray()
            .Select(provider => $"{provider.GetProperty("scheme").GetString()}={provider.GetProperty("displayName").GetString()}")
            .ToArray();

        providers.Should().BeEquivalentTo(["GitHub=GitHub Enterprise", "Twitter=X", "MintPlayer=MintPlayer ID"]);
    }

    [Theory]
    [InlineData(SparkLocalCredentials.Full)]
    [InlineData(SparkLocalCredentials.SignInOnly)]
    [InlineData(SparkLocalCredentials.Disabled)]
    public async Task Capabilities_reports_two_factor_when_its_endpoints_are_mapped(SparkLocalCredentials mode)
    {
        // manage/2fa is Microsoft's and is kept in every mode; the client also requires password sign-in.
        using var host = await StartAsync(mode);

        var body = await GetCapabilitiesAsync(host);

        body.GetProperty("twoFactor").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Every account route answers <c>IsEndpointMapped</c> by type, per mode: Microsoft's through its
    /// <see cref="SparkIdentityEndpoints"/> stand-in, Spark's through its endpoint class. Full maps them
    /// all, so a stand-in no route is tagged with fails here.
    /// </summary>
    [Theory]
    [InlineData(SparkLocalCredentials.Full, "Login,Refresh,TwoFactor,AuthenticatorUri,Info,UpdateInfo,SetPassword,Register,ResendConfirmationEmail,ForgotPassword,ResetPassword,ConfirmEmailLink,ConfirmEmail,Profile,UpdateProfile,PersonalData,DeleteAccount")]
    [InlineData(SparkLocalCredentials.SignInOnly, "Login,Refresh,TwoFactor,AuthenticatorUri,Info,UpdateInfo,SetPassword,ForgotPassword,ResetPassword,ConfirmEmailLink,ConfirmEmail,Profile,UpdateProfile,PersonalData,DeleteAccount")]
    [InlineData(SparkLocalCredentials.Disabled, "TwoFactor,AuthenticatorUri,Info,ConfirmEmailLink,ConfirmEmail,Profile,UpdateProfile,PersonalData,DeleteAccount")]
    public async Task Identity_endpoints_are_asked_by_type(SparkLocalCredentials mode, string expected)
    {
        using var host = await StartAsync(mode);
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>();

        Type[] spark =
        [
            typeof(Account.Register<>), typeof(Account.ResendConfirmationEmail<>), typeof(Account.ForgotPassword<>),
            typeof(Account.ResetPassword<>), typeof(Account.ConfirmEmailLink<>), typeof(Account.ConfirmEmail<>),
            typeof(Account.UpdateInfo<>), typeof(Account.SetPassword<>), typeof(Account.Profile<>),
            typeof(Account.UpdateProfile<>), typeof(Account.AuthenticatorUri<>), typeof(Account.PersonalData<>),
            typeof(Account.DeleteAccount<>),
        ];

        var mapped = typeof(SparkIdentityEndpoints).GetNestedTypes().Concat(spark)
            .Where(endpoints.IsEndpointMapped)
            .Select(type => type.Name.Split('`')[0]);

        mapped.Should().BeEquivalentTo(expected.Split(','));
    }

    [Fact]
    public async Task Capabilities_is_reachable_anonymously()
    {
        // It is the page an unauthenticated visitor lands on. Requiring auth to discover how to
        // authenticate would be circular.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);
        using var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/spark/auth/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
