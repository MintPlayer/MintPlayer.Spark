using System.Text.Json;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests;

/// <summary>
/// The passkeys page and the passkey ceremonies, driven through a real browser against a virtual
/// authenticator.
/// </summary>
/// <remarks>
/// <para>
/// This is the only coverage where a <em>real</em> WebAuthn credential passes through the stack.
/// Every server-side passkey test posts credential JSON that we wrote, so all of them would still
/// pass if our assumption about the wire shape were wrong — what the browser actually serialises
/// from <c>navigator.credentials.create()</c>, what
/// <c>PublicKeyCredential.parseCreationOptionsFromJSON</c> actually accepts, and whether the
/// ceremony cookie survives the 449 of a client-method retry, are only exercised here.
/// </para>
/// <para>
/// The page is the generic one (generic passkeys page PRD D1–D4, plan M6): <c>/account/passkeys</c>
/// forwards to <c>/po/passkeys/me</c>, Add is the action bar's <c>AddPasskey</c>, Rename and Remove
/// are the <c>my-passkeys</c> grid's row menu. Assertions read that query through
/// <c>/spark/queries/execute</c>, as the page does, rather than trusting what the page shows.
/// </para>
/// <para>
/// ⚠️ Each test signs in an account of its own: one virtual authenticator holds one credential per
/// account (a second enrollment is refused as <c>InvalidStateError</c>), and the shared admin must
/// not collect passkeys or lose its password. The E2E suite shares one rate-limit bucket, so these
/// tests stay deliberately few and do their setup through the API rather than by clicking.
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class PasskeyCeremonyTests
{
    private const int Timeout = 20_000;
    private const string PasskeysPage = "/po/passkeys/me";

    private readonly FleetE2ECollectionFixture _fixture;
    public PasskeyCeremonyTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>A new password account, signed in on <paramref name="page"/>; returns its user id.</summary>
    private async Task<string> SignInNewAccountAsync(IPage page)
    {
        var email = $"passkey-{Guid.NewGuid():N}@e2e.local";
        var userId = await _fixture.Host.SeedUserAsync(email, _fixture.Host.AdminPass, groupName: null);
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, email, _fixture.Host.AdminPass);
        return userId;
    }

    /// <summary>Opens the account area's passkeys path, which must land on the generic page.</summary>
    private static async Task OpenPasskeysPageAsync(IPage page)
    {
        await page.GotoAsync("/account/passkeys");
        await PollAsync(() => Task.FromResult(page.Url.EndsWith(PasskeysPage, StringComparison.Ordinal)),
            () => Task.FromResult($"/account/passkeys should forward to {PasskeysPage}, the page is at {page.Url}"));
    }

    /// <summary>Adds a passkey with the action bar's Add and waits until the query lists it.</summary>
    private async Task<(string Id, string? Name)> AddPasskeyAsync(IPage page)
    {
        var add = page.Locator(".spark-actionbar [data-action='AddPasskey']").Filter(new() { Visible = true });
        await add.WaitForAsync(new() { Timeout = Timeout });
        (await add.IsDisabledAsync()).Should().BeFalse("the virtual authenticator makes webauthn.create supported");
        await add.ClickAsync();

        // The query, not the page: a row there is proof the attestation of pass 2 was stored, rather
        // than that the ceremony merely started.
        IReadOnlyList<(string Id, string? Name)> listed = [];
        await PollAsync(async () => (listed = await MyPasskeysAsync(page)).Count == 1,
            () => Task.FromResult($"my-passkeys should list the new passkey, it lists {listed.Count}"));
        return listed[0];
    }

    /// <summary>The caller's passkeys through the generic <c>my-passkeys</c> query, with the browser's session.</summary>
    private async Task<IReadOnlyList<(string Id, string? Name)>> MyPasskeysAsync(IPage page)
    {
        var response = await page.APIRequest.PostAsync($"{_fixture.Host.FleetUrl}/spark/queries/execute",
            new() { DataObject = new { queryId = "my-passkeys", skip = 0, take = 50 } });
        response.Status.Should().Be(200);

        var body = (await response.JsonAsync())!.Value;
        var result = body.TryGetProperty("result", out var envelope) ? envelope : body;
        return [.. result.GetProperty("items").EnumerateArray().Select(item => (
            item.GetProperty("id").GetString()!,
            item.GetProperty("values").EnumerateArray()
                .FirstOrDefault(v => v.GetProperty("key").GetString() == "Name") is { ValueKind: JsonValueKind.Object } name
                && name.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null))];
    }

    /// <summary>Opens the row menu of the grid's only row and picks <paramref name="action"/>.</summary>
    private static async Task ChooseRowActionAsync(IPage page, string action)
    {
        var row = page.Locator("tbody tr[data-row-key]:not([data-placeholder='true'])");
        await PollAsync(async () => await row.CountAsync() == 1,
            async () => $"the passkeys grid should show one row, it shows {await row.CountAsync()}");

        await row.First.Locator(".spark-row-menu-toggle").ClickAsync();
        var item = page.Locator($".spark-row-menu [data-action='{action}']");
        await item.WaitForAsync(new() { Timeout = Timeout });
        await item.ClickAsync();
    }

    [Fact]
    public async Task A_passkey_added_on_the_generic_page_signs_in()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await using var authenticator = await VirtualAuthenticator.AttachAsync(page);
        await SignInNewAccountAsync(page);

        await OpenPasskeysPageAsync(page);
        var added = await AddPasskeyAsync(page);

        // Ground truth from the device, not from our database: a credential really was created.
        (await authenticator.CredentialCountAsync())
            .Should().Be(1, "the browser must have created a credential on the authenticator");
        added.Name.Should().Be("Unnamed passkey", "Add names nothing; Rename does");

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
        await passkeyButton.WaitForAsync(new() { Timeout = Timeout });
        await passkeyButton.ClickAsync();

        (await BrowserSignIn.WaitForAuthenticatedAsync(page, _fixture.Host.FleetUrl, TimeSpan.FromSeconds(20)))
            .Should().BeTrue("the passkey assertion should have established a session with no username and no password");
    }

    /// <summary>
    /// Rename is a retry form (<c>PasskeyRename</c>) whose Cancel is the framework's translated one,
    /// not an option the action spelled; Remove asks the <c>actions.json</c> confirmation first. The
    /// account has a password, so its only passkey may go.
    /// </summary>
    [Fact]
    public async Task A_passkey_is_renamed_through_the_retry_form_and_removed_after_confirming()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await using var authenticator = await VirtualAuthenticator.AttachAsync(page);
        await SignInNewAccountAsync(page);
        await OpenPasskeysPageAsync(page);
        await AddPasskeyAsync(page);

        // ---- Cancel changes nothing.
        await ChooseRowActionAsync(page, "RenamePasskey");
        var name = page.Locator("input#Name");
        await name.WaitForAsync(new() { Timeout = Timeout });
        await name.FillAsync("Discarded");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await name.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = Timeout });
        (await MyPasskeysAsync(page)).Should().ContainSingle().Which.Name.Should().Be("Unnamed passkey");

        // ---- Save stores the name, and the grid is refreshed by the action.
        await ChooseRowActionAsync(page, "RenamePasskey");
        await name.WaitForAsync(new() { Timeout = Timeout });
        await name.FillAsync("Work laptop");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        IReadOnlyList<(string Id, string? Name)> listed = [];
        await PollAsync(async () => (listed = await MyPasskeysAsync(page)) is [{ Name: "Work laptop" }],
            () => Task.FromResult($"the rename should be stored, my-passkeys lists {string.Join(", ", listed.Select(p => p.Name))}"));
        await page.Locator("tbody").GetByText("Work laptop").WaitForAsync(new() { Timeout = Timeout });

        // ---- Remove asks first, then removes.
        var confirmations = new List<string>();
        page.Dialog += (_, dialog) => { confirmations.Add(dialog.Message); _ = dialog.AcceptAsync(); };
        await ChooseRowActionAsync(page, "RemovePasskey");
        await PollAsync(async () => (await MyPasskeysAsync(page)).Count == 0,
            () => Task.FromResult("the passkey should be removed: the account's password is still a way in"));
        confirmations.Should().ContainSingle().Which.Should().Contain("Remove this passkey?");
    }

    /// <summary>
    /// ⚠️ An account whose only way in is the passkey keeps it: removing it would lock the owner out
    /// for good. The refusal is a notification, and the row stays.
    /// </summary>
    [Fact]
    public async Task The_last_way_in_is_not_removed()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await using var authenticator = await VirtualAuthenticator.AttachAsync(page);
        var userId = await SignInNewAccountAsync(page);
        await OpenPasskeysPageAsync(page);
        await AddPasskeyAsync(page);

        // No password, no external login: the passkey is the account's last credential.
        await _fixture.Host.RemovePasswordAsync(userId);

        page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
        await ChooseRowActionAsync(page, "RemovePasskey");

        await page.Locator("spark-toast-container")
            .GetByText("This is the only way you can sign in, so it cannot be removed.", new() { Exact = false })
            .WaitForAsync(new() { Timeout = Timeout });
        (await MyPasskeysAsync(page)).Should().HaveCount(1, "the last way in must not be removed");
        (await authenticator.CredentialCountAsync()).Should().Be(1);
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

    /// <summary>Polls rather than waiting on a navigation event (see <c>ConcurrentEditConflictTests.PollAsync</c>).</summary>
    private static async Task PollAsync(Func<Task<bool>> condition, Func<Task<string>> failure)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Timeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                var message = await failure();
                false.Should().BeTrue(message);
            }
            await Task.Delay(100);
        }
    }
}
