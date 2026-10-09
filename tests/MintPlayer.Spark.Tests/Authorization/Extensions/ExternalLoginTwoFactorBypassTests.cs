using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #490 D11 — the per-user skip of the application's two-factor step after an external sign-in
/// (<c>GET/POST /spark/auth/manage/external-login-two-factor</c>, legacy MintPlayer's
/// <c>Bypass2faForExternalLogin</c>). Mapped only while <c>AllowUserBypass</c> is on, and switching it on
/// needs a valid authenticator code: a stolen session alone must not weaken the account.
/// </summary>
public class ExternalLoginTwoFactorBypassTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string Route = "/spark/auth/manage/external-login-two-factor";

    private readonly ExternalTwoFactorTestHost host = new();
    private UserManager<SparkUser> Um => host.UserManager;

    private static readonly Dictionary<string, string?> BypassAllowed = new()
    {
        ["Spark:Auth:ExternalLogin:TwoFactor:AllowUserBypass"] = "true",
    };

    private SparkUser ArrangeUser(bool twoFactorEnabled = true, bool bypass = false)
    {
        var user = new SparkUser
        {
            Id = "users/alice",
            UserName = "alice",
            TwoFactorEnabled = twoFactorEnabled,
            BypassTwoFactorForExternalLogin = bypass,
        };
        Um.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        Um.UpdateAsync(user).Returns(IdentityResult.Success);
        Um.VerifyTwoFactorTokenAsync(user, Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        Um.VerifyTwoFactorTokenAsync(user, Arg.Any<string>(), "123456").Returns(true);
        return user;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData(null, null)]      // the defaults: the step is on, bypassing is not allowed
    [InlineData("false", "true")] // bypassing explicitly not allowed
    [InlineData("true", "false")] // allowed, but the step itself is off, so there is nothing to skip
    public async Task The_endpoints_are_not_mapped_unless_the_step_is_on_and_bypass_is_allowed(string? allowBypass, string? enabled)
    {
        ArrangeUser();
        var configuration = new Dictionary<string, string?>();
        if (allowBypass is not null) configuration["Spark:Auth:ExternalLogin:TwoFactor:AllowUserBypass"] = allowBypass;
        if (enabled is not null) configuration["Spark:Auth:ExternalLogin:TwoFactor:Enabled"] = enabled;
        using var server = await host.StartAsync(Store, configuration: configuration);
        var client = server.CreateClient();

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Route, new { bypass = false })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var capabilities = await BodyAsync(await client.GetAsync("/spark/auth/capabilities"));
        capabilities.GetProperty("externalLoginTwoFactorBypass").GetBoolean().Should().BeFalse(
            "the account page must not offer a switch whose endpoint is absent");
        await Um.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task With_bypass_allowed_the_state_is_reported_and_advertised()
    {
        ArrangeUser(bypass: true);
        using var server = await host.StartAsync(Store, configuration: BypassAllowed);
        var client = server.CreateClient();

        var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("bypass").GetBoolean().Should().BeTrue();
        body.GetProperty("twoFactorEnabled").GetBoolean().Should().BeTrue();

        (await BodyAsync(await client.GetAsync("/spark/auth/capabilities")))
            .GetProperty("externalLoginTwoFactorBypass").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000")]
    public async Task Switching_the_skip_on_without_a_valid_code_is_refused(string? code)
    {
        var user = ArrangeUser();
        using var server = await host.StartAsync(Store, configuration: BypassAllowed);

        var response = await server.CreateClient().PostAsJsonAsync(Route, new { bypass = true, code });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        user.BypassTwoFactorForExternalLogin.Should().BeFalse();
        await Um.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task Switching_the_skip_on_with_a_valid_code_saves_it()
    {
        var user = ArrangeUser();
        using var server = await host.StartAsync(Store, configuration: BypassAllowed);

        var response = await server.CreateClient().PostAsJsonAsync(Route, new { bypass = true, code = "123 456" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).GetProperty("bypass").GetBoolean().Should().BeTrue();
        user.BypassTwoFactorForExternalLogin.Should().BeTrue();
        await Um.Received().VerifyTwoFactorTokenAsync(user, Arg.Any<string>(), "123456");
        await Um.Received().UpdateAsync(user);
    }

    [Fact]
    public async Task Switching_the_skip_on_for_an_account_without_two_factor_is_refused()
    {
        var user = ArrangeUser(twoFactorEnabled: false);
        using var server = await host.StartAsync(Store, configuration: BypassAllowed);

        var response = await server.CreateClient().PostAsJsonAsync(Route, new { bypass = true, code = "123456" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        user.BypassTwoFactorForExternalLogin.Should().BeFalse();
        await Um.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task Switching_the_skip_off_needs_no_code()
    {
        var user = ArrangeUser(bypass: true);
        using var server = await host.StartAsync(Store, configuration: BypassAllowed);

        var response = await server.CreateClient().PostAsJsonAsync(Route, new { bypass = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        user.BypassTwoFactorForExternalLogin.Should().BeFalse();
        await Um.DidNotReceiveWithAnyArgs().VerifyTwoFactorTokenAsync(default!, default!, default!);
        await Um.Received().UpdateAsync(user);
    }
}
