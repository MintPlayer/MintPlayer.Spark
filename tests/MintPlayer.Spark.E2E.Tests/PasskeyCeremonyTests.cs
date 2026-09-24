using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests;

/// <summary>
/// The passkey ceremony, driven through a real browser against a virtual authenticator.
/// </summary>
/// <remarks>
/// <para>
/// This is the only coverage where a <em>real</em> WebAuthn credential passes through the stack.
/// Every server-side passkey test posts credential JSON that we wrote, so all of them would still
/// pass if our assumption about the wire shape were wrong — what the browser actually serialises
/// from <c>navigator.credentials.create()</c>, and what
/// <c>PublicKeyCredential.parseCreationOptionsFromJSON</c> actually accepts, are only exercised here.
/// </para>
/// <para>
/// ⚠️ The E2E suite shares one rate-limit bucket (150 requests / 10s on 127.0.0.1), so these tests
/// stay deliberately few and do their setup through the API rather than by clicking.
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class PasskeyCeremonyTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public PasskeyCeremonyTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>Enrolls a passkey through the browser and returns its base64url credential id.</summary>
    private async Task<string> EnrollAsync(IPage page)
    {
        await page.GotoAsync("/passkeys");

        // The page is lazy-loaded behind withPasskeys(); wait for the control rather than a timeout.
        var addButton = page.GetByRole(AriaRole.Button, new() { NameString = "Add a passkey" });
        await addButton.WaitForAsync(new() { Timeout = 20_000 });
        await addButton.ClickAsync();

        // The list re-renders from the server after enrollment, so the row appearing is proof the
        // round trip completed rather than that the ceremony merely started.
        var row = page.Locator("button:has-text('Remove')").First;
        await row.WaitForAsync(new() { Timeout = 20_000 });

        var listed = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/passkeys");
        listed.Status.Should().Be(200);

        var passkeys = await listed.JsonAsync();
        var first = passkeys!.Value.EnumerateArray().First();
        return first.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_passkey_can_be_enrolled_and_then_used_to_sign_in()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await using var authenticator = await VirtualAuthenticator.AttachAsync(page);

        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl,
            _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        await EnrollAsync(page);

        // Ground truth from the device, not from our database: a credential really was created.
        (await authenticator.CredentialCountAsync())
            .Should().Be(1, "the browser must have created a credential on the authenticator");

        // Drop the session by clearing cookies rather than by calling /spark/auth/logout.
        //
        // ⚠️ Not a shortcut around a broken endpoint: /logout is antiforgery-gated, so a bare
        // APIRequest POST is correctly refused for want of an X-XSRF-TOKEN header. Driving that
        // properly belongs to a test about logout; here it would only add a way to fail for a reason
        // that has nothing to do with passkeys. The virtual authenticator is bound to the context,
        // not to the cookies, so it survives.
        await page.Context.ClearCookiesAsync();

        (await BrowserSignIn.WaitForAuthenticatedAsync(page, _fixture.Host.FleetUrl, TimeSpan.FromSeconds(2)))
            .Should().BeFalse("the session must really be gone, or signing in with the passkey proves nothing");

        await page.GotoAsync("/login");
        var passkeyButton = page.GetByRole(AriaRole.Button, new() { NameString = "Sign in with a passkey" });
        await passkeyButton.WaitForAsync(new() { Timeout = 20_000 });
        await passkeyButton.ClickAsync();

        (await BrowserSignIn.WaitForAuthenticatedAsync(page, _fixture.Host.FleetUrl, TimeSpan.FromSeconds(20)))
            .Should().BeTrue("the passkey assertion should have established a session with no username and no password");
    }

    /// <summary>
    /// The request-options endpoint must not vary with who is asking — that is what stops it being a
    /// user-existence oracle. Anonymous by design, so no sign-in first.
    /// </summary>
    [Fact]
    public async Task Request_options_are_anonymous_and_carry_no_credentials()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.PostAsync(
            $"{_fixture.Host.FleetUrl}/spark/auth/passkeys/request-options");

        response.Status.Should().Be(200);
        var body = await response.JsonAsync();

        body!.Value.GetProperty("allowCredentials").GetArrayLength()
            .Should().Be(0, "a discoverable-credential request must not disclose which credentials exist");
        body.Value.GetProperty("userVerification").GetString()
            .Should().Be("required", "asserted on the wire rather than inferred from the framework default");
    }

    /// <summary>
    /// The capability the sign-in page reads. Derived from the live route table, so this also pins
    /// that Fleet actually mounted the surface.
    /// </summary>
    [Fact]
    public async Task Capabilities_report_passkeys_enabled_for_Fleet()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/capabilities");

        response.Status.Should().Be(200);
        (await response.JsonAsync())!.Value.GetProperty("passkeys").GetBoolean().Should().BeTrue();
    }
}
