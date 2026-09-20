using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// The <see cref="Configuration.SparkExternalLoginLinking.ConfirmByEmail"/> mechanism, driven
/// directly rather than through the endpoints, because what matters here is the half-life of a
/// mailed capability: it survives a process restart, an unbounded wait and a second click, and
/// every one of those is a property of the document rather than of the request that made it.
/// </summary>
/// <remarks>
/// The RavenDB store is real — the pending document's key is <em>derived</em>, and the whole reason
/// for that (a load rather than a possibly-stale query) is invisible against an in-memory fake.
/// </remarks>
public class SparkExternalLoginLinkerTests : SparkTestDriver
{
    private const string Provider = "GitLab";
    private const string ProviderKey = "gl-17";

    /// <summary>A clock the test moves, so "the link expired" does not take an hour to assert.</summary>
    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingSender : ISparkLinkConfirmationSender<SparkUser>
    {
        public List<(SparkUser User, string Provider, string? Identity, string Link)> Sent { get; } = [];

        public Task SendLinkConfirmationAsync(
            SparkUser user, string providerDisplayName, string? providerIdentity,
            string confirmationLink, CancellationToken cancellationToken = default)
        {
            Sent.Add((user, providerDisplayName, providerIdentity, confirmationLink));
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        SparkExternalLoginLinker<SparkUser> Linker,
        RecordingSender Sender,
        StoppedClock Clock,
        UserManager<SparkUser> UserManager,
        SparkUser User);

    private Harness NewHarness()
    {
        // Unique per harness: the database is shared across the class and the pending document's key
        // is derived from the account, so a fixed id would have tests overwriting each other's link.
        var user = new SparkUser
        {
            Id = "users/alice-" + Guid.NewGuid().ToString("N"),
            Email = "alice@test.org",
            UserName = "alice",
        };
        var userManager = Substitute.For<UserManager<SparkUser>>(
            Substitute.For<IUserStore<SparkUser>>(),
            Options.Create(new IdentityOptions()),
            Substitute.For<IPasswordHasher<SparkUser>>(),
            Array.Empty<IUserValidator<SparkUser>>(),
            Array.Empty<IPasswordValidator<SparkUser>>(),
            Substitute.For<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<SparkUser>>>());
        userManager.FindByIdAsync(user.Id).Returns(user);
        userManager.AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);

        var sender = new RecordingSender();
        var clock = new StoppedClock(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

        return new Harness(
            new SparkExternalLoginLinker<SparkUser>(Store, userManager, clock, sender),
            sender, clock, userManager, user);
    }

    private static Task<SparkLinkRequestOutcome> RequestAsync(Harness h, string providerKey = ProviderKey)
        => h.Linker.RequestLinkAsync(h.User, Provider, providerKey, Provider, "alice-on-gitlab", t => $"https://app.test/c?token={t}");

    private static string TokenOf(RecordingSender sender, int index = 0)
        => sender.Sent[index].Link.Split("token=")[1];

    [Fact]
    public async Task A_request_mails_a_link_and_the_token_confirms_it()
    {
        var h = NewHarness();

        var outcome = await RequestAsync(h);

        outcome.Should().Be(SparkLinkRequestOutcome.ConfirmationSent);
        h.Sender.Sent.Should().ContainSingle();
        h.Sender.Sent[0].User.Should().BeSameAs(h.User,
            "the mail goes to the address on the existing account, which the sender reads from the user");

        var result = await h.Linker.ConfirmAsync(TokenOf(h.Sender));

        result.Outcome.Should().Be(SparkLinkConfirmationOutcome.Linked);
        result.UserId.Should().Be(h.User.Id);
        await h.UserManager.Received(1).AddLoginAsync(h.User, Arg.Is<UserLoginInfo>(li =>
            li.LoginProvider == Provider && li.ProviderKey == ProviderKey));
    }

    /// <summary>
    /// ⚠️ The property the whole design rests on: what gets attached comes from the <b>document</b>,
    /// so it is the identity the confirmation was issued for even when the request presents another.
    /// </summary>
    [Fact]
    public async Task A_confirmation_carrying_a_different_identity_is_refused()
    {
        var h = NewHarness();
        await RequestAsync(h);

        var result = await h.Linker.ConfirmAsync(TokenOf(h.Sender), presentedProviderKey: "gl-999");

        result.Outcome.Should().Be(SparkLinkConfirmationOutcome.ProviderMismatch);
        await h.UserManager.DidNotReceive().AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>());
    }

    [Fact]
    public async Task A_second_attempt_while_one_is_pending_sends_no_second_mail()
    {
        var h = NewHarness();

        await RequestAsync(h);
        var second = await RequestAsync(h);

        second.Should().Be(SparkLinkRequestOutcome.ConfirmationSent,
            "the caller must not be able to tell a fresh send from a suppressed one");
        h.Sender.Sent.Should().ContainSingle(
            "repeated sign-ins with someone else's address would otherwise flood their mailbox with "
            + "messages that each read as an account-takeover attempt");
    }

    [Fact]
    public async Task A_request_for_a_different_identity_is_a_separate_pending_link()
    {
        var h = NewHarness();

        await RequestAsync(h);
        await RequestAsync(h, providerKey: "gl-42");

        h.Sender.Sent.Should().HaveCount(2,
            "the suppression above is keyed on the identity asking, not on the account");
    }

    [Fact]
    public async Task A_link_cannot_be_followed_twice()
    {
        var h = NewHarness();
        await RequestAsync(h);
        var token = TokenOf(h.Sender);

        (await h.Linker.ConfirmAsync(token)).Outcome.Should().Be(SparkLinkConfirmationOutcome.Linked);
        var again = await h.Linker.ConfirmAsync(token);

        again.Outcome.Should().Be(SparkLinkConfirmationOutcome.AlreadyUsed,
            "a second click is a normal thing to do, and 'already done' is a better answer than 'broken'");
        again.UserId.Should().BeNull("a refused confirmation hands back no account id");
    }

    [Fact]
    public async Task A_link_stops_working_once_its_window_passes()
    {
        var h = NewHarness();
        await RequestAsync(h);
        var token = TokenOf(h.Sender);

        h.Clock.Now += SparkExternalLoginLinker<SparkUser>.ConfirmationWindow + TimeSpan.FromMinutes(1);

        (await h.Linker.ConfirmAsync(token)).Outcome
            .Should().Be(SparkLinkConfirmationOutcome.InvalidOrExpired);
        await h.UserManager.DidNotReceive().AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>());
    }

    /// <summary>
    /// A spent link is replaced rather than accumulated, so asking again after confirming works.
    /// </summary>
    [Fact]
    public async Task A_new_request_after_a_spent_link_sends_again()
    {
        var h = NewHarness();
        await RequestAsync(h);
        await h.Linker.ConfirmAsync(TokenOf(h.Sender));

        await RequestAsync(h);

        h.Sender.Sent.Should().HaveCount(2);
        (await h.Linker.ConfirmAsync(TokenOf(h.Sender, 1))).Outcome
            .Should().Be(SparkLinkConfirmationOutcome.Linked);
    }

    /// <summary>
    /// ⚠️ The key half of the token is interpolated into a document id. Anything that is not a
    /// 32-character lowercase hex string must be refused before it gets there, or a crafted token
    /// addresses any document in the database.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData(".secret")]
    [InlineData("deadbeef.")]
    [InlineData("users/alice.secret")]
    [InlineData("../../Users/alice.secret")]
    [InlineData("DEADBEEFDEADBEEFDEADBEEFDEADBEEF.secret")]
    public async Task A_malformed_token_is_refused_without_touching_the_store(string? token)
    {
        var h = NewHarness();

        (await h.Linker.ConfirmAsync(token)).Outcome
            .Should().Be(SparkLinkConfirmationOutcome.InvalidOrExpired);
    }

    [Fact]
    public async Task A_correct_key_with_the_wrong_secret_is_refused()
    {
        var h = NewHarness();
        await RequestAsync(h);
        var token = TokenOf(h.Sender);
        var forged = token[..token.IndexOf('.')] + ".not-the-secret";

        (await h.Linker.ConfirmAsync(forged)).Outcome
            .Should().Be(SparkLinkConfirmationOutcome.InvalidOrExpired,
                "an existing pending link must not be distinguishable from a missing one");
    }

    [Fact]
    public async Task The_stored_document_never_holds_the_mailed_token()
    {
        var h = NewHarness();
        await RequestAsync(h);
        var token = TokenOf(h.Sender);
        var secret = token[(token.IndexOf('.') + 1)..];

        using var session = Store.OpenAsyncSession();
        var pending = await session.LoadAsync<SparkPendingExternalLogin>(
            "SparkPendingExternalLogins/" + token[..token.IndexOf('.')]);

        pending.Should().NotBeNull("the mailed token's key half addresses the document directly");

        pending.TokenHash.Should().NotContain(secret,
            "a leaked backup must not yield something that can be presented");
        pending.ProviderKey.Should().Be(ProviderKey);
        pending.UserId.Should().Be(h.User.Id);
    }

    [Fact]
    public async Task A_vanished_account_is_reported_rather_than_linked()
    {
        var h = NewHarness();
        await RequestAsync(h);
        h.UserManager.FindByIdAsync(h.User.Id!).Returns((SparkUser?)null);

        (await h.Linker.ConfirmAsync(TokenOf(h.Sender))).Outcome
            .Should().Be(SparkLinkConfirmationOutcome.AccountGone);
    }

    /// <summary>
    /// Reaching the request path with no transport registered is a configuration that the startup
    /// guard exists to prevent. If it is reached anyway, writing a pending document nobody will be
    /// told about is worse than failing.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_sender_registered_throws_rather_than_writing_silently()
    {
        var h = NewHarness();
        var linker = new SparkExternalLoginLinker<SparkUser>(Store, h.UserManager, h.Clock, confirmationSender: null);

        var act = async () => await linker.RequestLinkAsync(
            h.User, Provider, ProviderKey, Provider, null, t => t);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ISparkLinkConfirmationSender*");
    }
}
