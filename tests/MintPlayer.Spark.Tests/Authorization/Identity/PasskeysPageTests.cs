using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.Actions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.CustomActions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// The generic passkeys page (generic passkeys page PRD D1–D4): the <c>Passkeys</c> page, the
/// <c>my-passkeys</c> query and the three custom actions, over <see cref="ISparkPasskeyAccount"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the retired management endpoints' tests (PRD D8, plan M6): the same
/// refusals (a ceremony that throws, a credential held elsewhere, the last-credential guard) and the
/// same 64-character name rule, now reached through the action pipeline's retry instead of HTTP.
/// </para>
/// <para>
/// Each pass is a fresh action, retry accessor and account, as a real request scope is: the action
/// re-runs from the top on pass 2, which is exactly where the D7 trap lives.
/// </para>
/// </remarks>
public class PasskeysPageTests
{
    private static readonly byte[] AliceCredential = [1, 2, 3, 4];
    private static readonly byte[] BobCredential = [9, 9, 9, 9];
    private static readonly string AliceCredentialText = Encode(AliceCredential);
    private static readonly string BobCredentialText = Encode(BobCredential);

    private readonly SparkUser alice = new() { Id = "users/alice", UserName = "alice" };
    private readonly UserManager<SparkUser> userManager = NewUserManagerStub();
    private readonly SignInManager<SparkUser> signInManager;
    private readonly SparkAuthenticationOptions options = new() { Passkeys = SparkPasskeys.Enabled, LocalCredentials = SparkLocalCredentials.Disabled };
    private ClaimsPrincipal principal = SignedIn("users/alice");
    private ClientAccessor client = new();
    private RetryAccessor retry = null!;

    public PasskeysPageTests()
    {
        signInManager = NewSignInManagerStub(userManager);
        userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(alice);
        userManager.GetUserIdAsync(alice).Returns(alice.Id!);
        userManager.GetUserNameAsync(alice).Returns(alice.UserName);
        userManager.GetPasskeysAsync(alice).Returns([Passkey(AliceCredential, "laptop")]);
        userManager.GetPasskeyAsync(alice, Arg.Is<byte[]>(id => id.SequenceEqual(AliceCredential))).Returns(Passkey(AliceCredential, "laptop"));
        userManager.GetLoginsAsync(alice).Returns([]);
        userManager.HasPasswordAsync(alice).Returns(false);
        userManager.AddOrUpdatePasskeyAsync(alice, Arg.Any<UserPasskeyInfo>()).Returns(IdentityResult.Success);
        userManager.RemovePasskeyAsync(alice, Arg.Any<byte[]>()).Returns(IdentityResult.Success);
        signInManager.MakePasskeyCreationOptionsAsync(Arg.Any<PasskeyUserEntity>()).Returns("{\"challenge\":\"abc\",\"timeout\":300000}");
        signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>())
            .Returns(PasskeyAttestationResult.Success(Passkey([5, 5, 5, 5], null), new PasskeyUserEntity { Id = "users/alice", Name = "alice", DisplayName = "alice" }));
    }

    #region Fixture

    private static string Encode(byte[] id) => Convert.ToBase64String(id).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ClaimsPrincipal SignedIn(string userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestCookie"));

    private static UserPasskeyInfo Passkey(byte[] id, string? name) =>
        new(id, [7, 7, 7], DateTimeOffset.UtcNow, 1, ["usb"], false, true, true, [3, 3], [4, 4]) { Name = name };

    /// <summary>One request scope: a new account, retry accessor and action, with the answers carried so far.</summary>
    private T Pass<T>(params RetryResult[] answered) where T : class
    {
        client = new ClientAccessor();
        retry = new RetryAccessor(client)
        {
            AnsweredResults = answered.Length == 0 ? null : answered.ToDictionary(r => r.Step),
        };

        var manager = Substitute.For<IManager>();
        manager.Retry.Returns(retry);
        manager.Client.Returns(client);
        manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.ArgAt<string>(0));
        manager.GetPersistentObjectAsync("Passkeys", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Page("Passkeys", "Description"));
        manager.GetPersistentObjectAsync("PasskeyRename", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Page("PasskeyRename", "Name"));

        var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } })
            .AddSingleton(userManager)
            .AddSingleton(signInManager)
            .AddSingleton(Options.Create(options))
            .AddSingleton(manager)
            .AddScoped<ISparkPasskeyAccount, SparkPasskeyAccount<SparkUser>>()
            .BuildServiceProvider();

        return ActivatorUtilities.CreateInstance<T>(services.CreateScope().ServiceProvider);
    }

    private static PersistentObject Page(string name, string attribute) => new()
    {
        Name = name,
        ObjectTypeId = Guid.NewGuid(),
        Attributes = [new PersistentObjectAttribute { Name = attribute, DataType = "string" }],
    };

    private static CustomActionArgs Selected(string id) => new()
    {
        SelectedItems = [new QueryResultItem { Id = id, Values = [] }],
    };

    private IEnumerable<NotifyOperation> Notifications => client.Operations.OfType<NotifyOperation>();

    private IEnumerable<string> Refreshed => client.Operations.OfType<RefreshQueryOperation>().Select(o => o.QueryId);

    private static RetryResult Answer(int step, string option, string? valueJson = null, PersistentObject? form = null) => new()
    {
        Step = step,
        Option = option,
        Value = valueJson is null ? null : JsonDocument.Parse(valueJson).RootElement.Clone(),
        PersistentObject = form,
    };

    #endregion

    #region The page and the query (M3)

    [Fact]
    public async Task The_page_is_the_callers_own_whatever_id_the_route_names()
    {
        var page = await Pass<PasskeysActions>().OnLoadAsync("users/bob", null);

        page.Should().NotBeNull();
        page!.Id.Should().Be(SparkPasskeysPage.ObjectId, "the id is the fixed 'me', never a user id");
        page["Description"].Value.Should().Be("auth.passkeysDescription");
        page.Breadcrumb.Should().Be("auth.passkeysTitle");
    }

    [Fact]
    public async Task The_page_is_a_404_for_an_anonymous_caller()
    {
        principal = new ClaimsPrincipal(new ClaimsIdentity());

        (await Pass<PasskeysActions>().OnLoadAsync("me", null)).Should().BeNull();
        await userManager.DidNotReceive().GetUserAsync(Arg.Any<ClaimsPrincipal>());
    }

    [Fact]
    public async Task The_page_is_a_404_when_passkeys_are_disabled()
    {
        options.Passkeys = SparkPasskeys.Disabled;

        (await Pass<PasskeysActions>().OnLoadAsync("me", null)).Should().BeNull();
    }

    [Fact]
    public async Task The_page_is_a_404_when_the_signed_in_user_no_longer_resolves()
    {
        userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns((SparkUser?)null);

        (await Pass<PasskeysActions>().OnLoadAsync("me", null)).Should().BeNull();
    }

    [Fact]
    public async Task The_query_lists_the_callers_passkeys_as_metadata_with_a_base64url_id()
    {
        userManager.GetPasskeysAsync(alice).Returns([Passkey(AliceCredential, "laptop"), Passkey([6, 6], null)]);

        var rows = (await Pass<PasskeyRowActions>().MyPasskeys()).ToList();

        rows.Select(r => r.Id).Should().Equal(AliceCredentialText, Encode([6, 6]));
        rows.Select(r => r.Name).Should().Equal("laptop", "auth.passkeyUnnamed");
        rows.Should().OnlyContain(r => r.Synced);
    }

    [Fact]
    public async Task The_query_is_empty_for_an_anonymous_caller_and_when_passkeys_are_disabled()
    {
        principal = new ClaimsPrincipal(new ClaimsIdentity());
        (await Pass<PasskeyRowActions>().MyPasskeys()).Should().BeEmpty();

        principal = SignedIn("users/alice");
        options.Passkeys = SparkPasskeys.Disabled;
        (await Pass<PasskeyRowActions>().MyPasskeys()).Should().BeEmpty();
    }

    [Fact]
    public void The_query_takes_no_arguments_so_it_cannot_read_a_user_from_the_page()
    {
        typeof(PasskeyRowActions).GetMethod(nameof(PasskeyRowActions.MyPasskeys))!.GetParameters()
            .Should().BeEmpty("a sub-query is handed the page as Parent; a user id read from it would be an IDOR (D2)");
    }

    #endregion

    #region AddPasskey: two passes through a client-method retry (M4, D3/D7)

    [Fact]
    public async Task Pass_one_hands_the_creation_options_to_the_browsers_webauthn_create()
    {
        var act = () => Pass<AddPasskeyAction>().ExecuteAsync(new CustomActionArgs());

        await act.Should().ThrowAsync<SparkRetryActionException>();
        var op = client.Operations.OfType<RetryOperation>().Should().ContainSingle().Which;
        op.ClientMethod.Should().Be("webauthn.create");
        op.Arguments!.Value.GetProperty("challenge").GetString().Should().Be("abc",
            "the options travel as the object itself, not as a JSON string");
        await signInManager.Received(1).MakePasskeyCreationOptionsAsync(Arg.Any<PasskeyUserEntity>());
    }

    /// <summary>
    /// ⚠️ The D7 trap: the action re-runs from the top on pass 2. Options made there would issue a new
    /// challenge and overwrite the ceremony cookie, and the attestation would fail.
    /// </summary>
    [Fact]
    public async Task Pass_two_attests_the_browsers_credential_without_minting_a_second_challenge()
    {
        await Pass<AddPasskeyAction>(Answer(0, "OK", "{\"id\":\"cred\",\"type\":\"public-key\"}")).ExecuteAsync(new CustomActionArgs());

        await signInManager.DidNotReceive().MakePasskeyCreationOptionsAsync(Arg.Any<PasskeyUserEntity>());
        await signInManager.Received(1).PerformPasskeyAttestationAsync(Arg.Is<string>(json => json.Contains("\"cred\"")));
        await userManager.Received(1).AddOrUpdatePasskeyAsync(alice, Arg.Any<UserPasskeyInfo>());
        Notifications.Should().ContainSingle(n => n.Kind == NotificationKind.Success && n.Message == "auth.passkeyAddedNotice");
        Refreshed.Should().Equal(SparkPasskeysPage.QueryAlias);
    }

    [Fact]
    public async Task A_cancelled_ceremony_ends_quietly()
    {
        await Pass<AddPasskeyAction>(Answer(0, "Cancel")).ExecuteAsync(new CustomActionArgs());

        await signInManager.DidNotReceive().PerformPasskeyAttestationAsync(Arg.Any<string>());
        Notifications.Should().BeEmpty("the user chose not to; that is not an error worth a banner (G5)");
        Refreshed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_ceremony_the_browser_reports_as_failed_is_said_to_have_failed()
    {
        await Pass<AddPasskeyAction>(Answer(0, "OK", "{\"error\":\"failed\"}")).ExecuteAsync(new CustomActionArgs());

        await signInManager.DidNotReceive().PerformPasskeyAttestationAsync(Arg.Any<string>());
        Notifications.Should().ContainSingle(n => n.Kind == NotificationKind.Error && n.Message == "auth.passkeyFailed");
    }

    /// <summary>
    /// The challenge is reused across the passes by design, and it is time-bound: SignInManager parks
    /// the ceremony state in Identity's TwoFactorUserId cookie, which lives 5 minutes (and is signed out
    /// once read). A pass 2 after that, or without the cookie, finds no attestation underway: Identity
    /// throws <see cref="InvalidOperationException"/>. That must be a friendly "try again", not a 500,
    /// and must not echo the framework's message.
    /// </summary>
    [Fact]
    public async Task A_pass_two_whose_ceremony_state_expired_or_is_missing_says_try_again_and_nothing_more()
    {
        signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>())
            .Returns<PasskeyAttestationResult>(_ => throw new InvalidOperationException(
                "No passkey attestation is underway. Make sure to call 'SignInManager.MakePasskeyCreationOptionsAsync()' to initiate a passkey attestation."));

        var act = () => Pass<AddPasskeyAction>(Answer(0, "OK", "{\"id\":\"cred\"}")).ExecuteAsync(new CustomActionArgs());

        await act.Should().NotThrowAsync();
        var notice = Notifications.Should().ContainSingle().Which;
        notice.Kind.Should().Be(NotificationKind.Error);
        notice.Message.Should().Be("auth.passkeyExpired");
        await userManager.DidNotReceive().AddOrUpdatePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<UserPasskeyInfo>());
        Refreshed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_ceremony_that_throws_or_fails_is_refused_rather_than_surfacing_the_exception()
    {
        signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>())
            .Returns<PasskeyAttestationResult>(_ => throw new PasskeyException("malformed"));
        var act = () => Pass<AddPasskeyAction>(Answer(0, "OK", "{\"id\":\"cred\"}")).ExecuteAsync(new CustomActionArgs());
        await act.Should().NotThrowAsync();
        Notifications.Should().ContainSingle(n => n.Message == "auth.passkeyFailed");

        signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>()).Returns(PasskeyAttestationResult.Fail(new PasskeyException("no")));
        await Pass<AddPasskeyAction>(Answer(0, "OK", "{\"id\":\"cred\"}")).ExecuteAsync(new CustomActionArgs());
        Notifications.Should().ContainSingle(n => n.Message == "auth.passkeyFailed");
        await userManager.DidNotReceive().AddOrUpdatePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<UserPasskeyInfo>());
    }

    [Fact]
    public async Task A_credential_already_held_by_another_account_is_refused_without_saying_so()
    {
        userManager.AddOrUpdatePasskeyAsync(alice, Arg.Any<UserPasskeyInfo>())
            .Returns<IdentityResult>(_ => throw new InvalidOperationException("taken by users/bob"));

        await Pass<AddPasskeyAction>(Answer(0, "OK", "{\"id\":\"cred\"}")).ExecuteAsync(new CustomActionArgs());

        var notice = Notifications.Should().ContainSingle().Which;
        notice.Message.Should().Be("auth.passkeyFailed", "the refusal must not become an oracle for who owns a credential");
    }

    #endregion

    #region RenamePasskey and RemovePasskey (M4, D4)

    [Fact]
    public async Task Rename_prompts_with_the_PasskeyRename_form_filled_with_the_current_name()
    {
        var act = () => Pass<RenamePasskeyAction>().ExecuteAsync(Selected(AliceCredentialText));

        await act.Should().ThrowAsync<SparkRetryActionException>();
        var op = client.Operations.OfType<RetryOperation>().Should().ContainSingle().Which;
        op.PersistentObject!.Name.Should().Be("PasskeyRename");
        op.PersistentObject["Name"].Value.Should().Be("laptop");
        // Save is the action's; Cancel is the client's own translated button, never an untranslated option.
        op.Options.Should().Equal("auth.passkeySave");
        op.Cancellable.Should().BeTrue();
    }

    [Fact]
    public async Task Rename_stores_the_sanitized_name_and_refreshes_the_grid()
    {
        var form = Page("PasskeyRename", "Name");
        form["Name"].Value = "  desk\u0007top " + new string('x', 100);
        UserPasskeyInfo? stored = null;
        userManager.AddOrUpdatePasskeyAsync(alice, Arg.Do<UserPasskeyInfo>(p => stored = p)).Returns(IdentityResult.Success);

        await Pass<RenamePasskeyAction>(Answer(0, "auth.passkeySave", form: form)).ExecuteAsync(Selected(AliceCredentialText));

        stored!.Name.Should().StartWith("desktop");
        stored.Name!.Length.Should().Be(64, "the 64-character limit survives the move off the endpoint (G5)");
        Refreshed.Should().Equal(SparkPasskeysPage.QueryAlias);
    }

    [Fact]
    public async Task A_cancelled_rename_changes_nothing()
    {
        await Pass<RenamePasskeyAction>(Answer(0, "Cancel", form: Page("PasskeyRename", "Name"))).ExecuteAsync(Selected(AliceCredentialText));

        await userManager.DidNotReceive().AddOrUpdatePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<UserPasskeyInfo>());
        Notifications.Should().BeEmpty();
    }

    /// <summary>⚠️ One passkey, no external login, no password: removing it locks the account out for good.</summary>
    [Fact]
    public async Task Removing_the_only_way_in_is_refused_with_a_notification()
    {
        await Pass<RemovePasskeyAction>().ExecuteAsync(Selected(AliceCredentialText));

        await userManager.DidNotReceive().RemovePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<byte[]>());
        Notifications.Should().ContainSingle(n => n.Kind == NotificationKind.Error && n.Message == "auth.passkeyLastCredential");
        Refreshed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_usable_password_rescues_the_last_passkey()
    {
        options.LocalCredentials = SparkLocalCredentials.Full;
        userManager.HasPasswordAsync(alice).Returns(true);

        await Pass<RemovePasskeyAction>().ExecuteAsync(Selected(AliceCredentialText));

        await userManager.Received(1).RemovePasskeyAsync(alice, Arg.Is<byte[]>(id => id.SequenceEqual(AliceCredential)));
        Notifications.Should().ContainSingle(n => n.Kind == NotificationKind.Success && n.Message == "auth.passkeyRemovedNotice");
        Refreshed.Should().Equal(SparkPasskeysPage.QueryAlias);
    }

    [Fact]
    public async Task Another_passkey_rescues_the_one_being_removed()
    {
        userManager.GetPasskeysAsync(alice).Returns([Passkey(AliceCredential, "laptop"), Passkey([6, 6, 6, 6], "phone")]);

        await Pass<RemovePasskeyAction>().ExecuteAsync(Selected(AliceCredentialText));

        await userManager.Received(1).RemovePasskeyAsync(alice, Arg.Is<byte[]>(id => id.SequenceEqual(AliceCredential)));
        Notifications.Should().ContainSingle(n => n.Kind == NotificationKind.Success && n.Message == "auth.passkeyRemovedNotice");
    }

    [Fact]
    public async Task An_external_login_rescues_the_last_passkey()
    {
        userManager.GetLoginsAsync(alice).Returns([new UserLoginInfo("GitHub", "gh-1", "GitHub")]);

        await Pass<RemovePasskeyAction>().ExecuteAsync(Selected(AliceCredentialText));

        await userManager.Received(1).RemovePasskeyAsync(alice, Arg.Any<byte[]>());
    }

    #endregion

    #region Isolation: nothing reaches another user's passkeys

    /// <summary>
    /// The selection is rebuilt from the caller's own query, but an action must not rely on that: a
    /// credential id from another account (or an undecodable one) is simply not among the caller's,
    /// so neither rename nor remove can touch it, and neither says whose it is.
    /// </summary>
    [Fact]
    public async Task Another_users_credential_cannot_be_renamed_or_removed()
    {
        foreach (var id in new[] { BobCredentialText, "a", "" })
        {
            await Pass<RemovePasskeyAction>().ExecuteAsync(Selected(id));
            await Pass<RenamePasskeyAction>(Answer(0, "auth.passkeySave", form: Page("PasskeyRename", "Name"))).ExecuteAsync(Selected(id));
        }

        await userManager.DidNotReceive().RemovePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<byte[]>());
        await userManager.DidNotReceive().AddOrUpdatePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<UserPasskeyInfo>());
        await userManager.DidNotReceive().GetPasskeyAsync(Arg.Any<SparkUser>(), Arg.Is<byte[]>(b => b.SequenceEqual(AliceCredential)));
    }

    [Fact]
    public async Task Every_read_starts_from_the_request_principal()
    {
        var bob = new SparkUser { Id = "users/bob", UserName = "bob" };
        principal = SignedIn("users/bob");
        userManager.GetUserAsync(Arg.Is<ClaimsPrincipal>(p => p.FindFirstValue(ClaimTypes.NameIdentifier) == "users/bob")).Returns(bob);
        userManager.GetPasskeysAsync(bob).Returns([]);

        (await Pass<PasskeyRowActions>().MyPasskeys()).Should().BeEmpty("bob's page lists bob's passkeys, and he has none");
        await Pass<RemovePasskeyAction>().ExecuteAsync(Selected(AliceCredentialText));

        await userManager.DidNotReceive().GetPasskeysAsync(alice);
        await userManager.DidNotReceive().RemovePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<byte[]>());
    }

    #endregion

    private static UserManager<SparkUser> NewUserManagerStub() => Substitute.For<UserManager<SparkUser>>(
        Substitute.For<IUserStore<SparkUser>>(),
        Options.Create(new IdentityOptions()),
        Substitute.For<IPasswordHasher<SparkUser>>(),
        Array.Empty<IUserValidator<SparkUser>>(),
        Array.Empty<IPasswordValidator<SparkUser>>(),
        Substitute.For<ILookupNormalizer>(),
        new IdentityErrorDescriber(),
        Substitute.For<IServiceProvider>(),
        Substitute.For<ILogger<UserManager<SparkUser>>>());

    private static SignInManager<SparkUser> NewSignInManagerStub(UserManager<SparkUser> userManager)
        => Substitute.For<SignInManager<SparkUser>>(
            userManager,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IUserClaimsPrincipalFactory<SparkUser>>(),
            Options.Create(new IdentityOptions()),
            Substitute.For<ILogger<SignInManager<SparkUser>>>(),
            Substitute.For<IAuthenticationSchemeProvider>(),
            Substitute.For<IUserConfirmation<SparkUser>>());
}
