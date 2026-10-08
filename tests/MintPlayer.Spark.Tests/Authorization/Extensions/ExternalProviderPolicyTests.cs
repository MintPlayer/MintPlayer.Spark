using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #460 D7 — per-provider verified-email trust and display-name user names — and spike SP-C, measured
/// against the handlers' own option defaults and claim actions (no live provider is contacted).
/// </summary>
public class ExternalProviderPolicyTests(ITestOutputHelper output) : SparkTestDriver
{
    private const string Stub = "StubProvider";

    #region Callback behaviour

    private async Task<AccountTestHost> StartAsync(SparkExternalProviderPolicy? policy, bool requireConfirmed = false)
        => await AccountTestHost.StartAsync(Store, SparkLocalCredentials.Disabled,
            configure: o =>
            {
                if (policy is not null)
                    o.ExternalProviders[Stub] = policy;
                o.RequireConfirmedEmail = requireConfirmed;
            },
            endpoints: e => e.MapGet("/test/handshake", async (Microsoft.AspNetCore.Http.HttpContext context, string email, string? name, bool verified) =>
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(email)))[..12]), new(ClaimTypes.Email, email) };
                if (name is not null) claims.Add(new(ClaimTypes.Name, name));
                if (verified) claims.Add(new("email_verified", "true"));
                var properties = new AuthenticationProperties();
                properties.Items["LoginProvider"] = Stub;
                await context.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, Stub)), properties);
            }));


    private static async Task<HttpResponseMessage> SignUpAsync(AccountTestHost host, string email, string? name, bool verified)
    {
        using var client = host.Server.CreateClient();
        var handshake = await client.GetAsync($"/test/handshake?email={Uri.EscapeDataString(email)}&verified={verified}"
            + (name is null ? "" : $"&name={Uri.EscapeDataString(name)}"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/spark/auth/external-login-callback?popup=1&returnUrl=%2F");
        request.Headers.Add("Cookie", AccountTestHost.CookieHeader(handshake));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_provider_without_a_signal_gets_an_unconfirmed_account_and_a_confirmation_mail()
    {
        await using var host = await StartAsync(SparkExternalProviderPolicy.WithoutVerifiedEmailSignal());

        var response = await SignUpAsync(host, "nosignal@example.com", "Jöhn Doe", verified: false);

        (await response.Content.ReadAsStringAsync()).Should().Contain("success: true", "RequireConfirmedEmail is off, so the person is signed in");
        var user = (await host.FindByEmailAsync("nosignal@example.com"))!;
        user.EmailConfirmed.Should().BeFalse();
        user.UserName.Should().Be("john-doe");
        user.RegistrationMethod.Should().Be(SparkRegistrationMethods.External(Stub));
        var mail = host.Mail.Sent.Should().ContainSingle().Which;
        mail.Kind.Should().Be("confirm");
        mail.Link.Should().StartWith($"{AccountTestHost.BaseUrl}/confirm-email?");
    }

    [Fact]
    public async Task With_RequireConfirmedEmail_the_unconfirmed_account_is_not_signed_in()
    {
        await using var host = await StartAsync(SparkExternalProviderPolicy.WithoutVerifiedEmailSignal(), requireConfirmed: true);

        var response = await SignUpAsync(host, "gated@example.com", "Gated", verified: false);

        (await response.Content.ReadAsStringAsync()).Should().Contain("error: 'confirm_email_sent'");
        AccountTestHost.CookieHeader(response).Should().NotContain(".AspNetCore.Identity.Application=");
        (await host.FindByEmailAsync("gated@example.com")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_scheme_nobody_described_still_refuses_an_unverified_email()
    {
        await using var host = await StartAsync(policy: null);

        var response = await SignUpAsync(host, "unverified@example.com", "Someone", verified: false);

        (await response.Content.ReadAsStringAsync()).Should().Contain("error: 'email_not_verified'");
        (await host.FindByEmailAsync("unverified@example.com")).Should().BeNull();
        host.Mail.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Display_name_slugs_are_unique_and_never_the_email_local_part()
    {
        await using var host = await StartAsync(policy: null);

        await SignUpAsync(host, "first@example.com", "Jane Doe", verified: true);
        await WaitForIndexesAsync();
        await SignUpAsync(host, "second@example.com", "Jane  DOE!", verified: true);
        await WaitForIndexesAsync();
        await SignUpAsync(host, "local-part@example.com", name: null, verified: true);

        (await host.FindByEmailAsync("first@example.com"))!.UserName.Should().Be("jane-doe");
        (await host.FindByEmailAsync("second@example.com"))!.UserName.Should().Be("jane-doe-2");
        (await host.FindByEmailAsync("local-part@example.com"))!.UserName.Should().NotContain("local-part");
    }

    [Fact]
    public async Task A_display_name_that_is_an_email_address_is_not_used_for_the_user_name()
    {
        // G-Q22: the user name is public. Slugging "jane@example.com" would publish it as jane-example-com.
        await using var host = await StartAsync(policy: null);

        await SignUpAsync(host, "jane@example.com", "jane@example.com", verified: true);

        (await host.FindByEmailAsync("jane@example.com"))!.UserName.Should().MatchRegex("^user-[0-9a-f]{6}$");
    }

    [Fact]
    public async Task A_provider_handle_is_used_verbatim_suffixed_when_taken_and_never_when_it_is_an_email()
    {
        await using var host = await StartAsync(new SparkExternalProviderPolicy
        {
            EmailVerification = _ => SparkEmailVerification.Verified,
            UserName = SparkUserNameSource.ProviderHandle,
        });
        await host.CreateUserAsync("octocat", "local-octocat@example.com");
        await WaitForIndexesAsync();

        await SignUpAsync(host, "fresh@example.com", "monalisa", verified: true);
        await SignUpAsync(host, "clash@example.com", "octocat", verified: true);
        await SignUpAsync(host, "shaped@example.com", "shaped@example.com", verified: true);

        (await host.FindByEmailAsync("fresh@example.com"))!.UserName.Should().Be("monalisa");
        (await host.FindByEmailAsync("clash@example.com"))!.UserName.Should().Be("octocat-2");
        (await host.FindByEmailAsync("shaped@example.com"))!.UserName.Should().MatchRegex("^user-[0-9a-f]{6}$");
    }

    [Theory]
    [InlineData("Jöhn Doe", "john-doe")]
    [InlineData("  ---  ", "user")]
    [InlineData("李小龍", "user")]
    [InlineData("O'Brien-Smith", "o-brien-smith")]
    public void Slugify(string input, string expected) => SparkExternalProviderPolicy.Slugify(input).Should().Be(expected);

    #endregion

    #region SP-C — what each preset reads

    private static ServiceProvider Presets()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddIdentityCore<SparkUser>();
        var identity = TestSparkAuth.Builder(services);
        identity.AddGitHub(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        identity.AddGoogle(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        identity.AddMicrosoftAccount(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        identity.AddFacebook(o => { o.AppId = "id"; o.AppSecret = "secret"; });
        identity.AddTwitter(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        identity.AddLinkedIn(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        return services.BuildServiceProvider();
    }

    private static string Describe(ClaimActionCollection actions)
        => string.Join(", ", actions.Select(a => a is JsonKeyClaimAction json ? $"{json.JsonKey}->{a.ClaimType}" : $"{a.GetType().Name}->{a.ClaimType}"));

    [Fact]
    public void SP_C_measure_the_handler_defaults()
    {
        using var provider = Presets();
        var google = provider.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme);
        var microsoft = provider.GetRequiredService<IOptionsMonitor<MicrosoftAccountOptions>>().Get(MicrosoftAccountDefaults.AuthenticationScheme);
        var facebook = provider.GetRequiredService<IOptionsMonitor<FacebookOptions>>().Get(FacebookDefaults.AuthenticationScheme);
        var twitter = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("Twitter");
        var linkedIn = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("LinkedIn");

        output.WriteLine($"GOOGLE userinfo={google.UserInformationEndpoint} scopes={string.Join(" ", google.Scope)} claims=[{Describe(google.ClaimActions)}]");
        output.WriteLine($"MICROSOFT authorize={microsoft.AuthorizationEndpoint} userinfo={microsoft.UserInformationEndpoint} scopes={string.Join(" ", microsoft.Scope)} claims=[{Describe(microsoft.ClaimActions)}]");
        output.WriteLine($"FACEBOOK userinfo={facebook.UserInformationEndpoint} fields={string.Join(",", facebook.Fields)} scopes={string.Join(" ", facebook.Scope)} claims=[{Describe(facebook.ClaimActions)}]");
        output.WriteLine($"TWITTER authorize={twitter.AuthorizationEndpoint} userinfo={twitter.UserInformationEndpoint} pkce={twitter.UsePkce} scopes={string.Join(" ", twitter.Scope)} claims=[{Describe(twitter.ClaimActions)}]");
        output.WriteLine($"LINKEDIN userinfo={linkedIn.UserInformationEndpoint} scopes={string.Join(" ", linkedIn.Scope)} claims=[{Describe(linkedIn.ClaimActions)}]");

        var registrations = provider.GetServices<SparkExternalProviderRegistration>().ToArray();
        output.WriteLine($"REGISTRATIONS {string.Join(", ", registrations.Select(r => $"{r.Scheme}:{r.Policy.UserName}"))}");
    }

    #endregion
}
