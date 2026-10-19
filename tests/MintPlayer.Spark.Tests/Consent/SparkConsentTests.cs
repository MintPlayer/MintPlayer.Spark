using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Consent;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Visitor;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Consent;

/// <summary>
/// The consent primitive (commerce PRD X21, GA9): an undecided purpose is never granted; decisions go stale with the
/// policy version and the validity period; a visitor's decisions move onto the account at sign-in with the newer one
/// winning; an unsubscribe token withdraws exactly its purpose and cannot be forged; erasure removes the records.
/// </summary>
public class SparkConsentTests : SparkTestDriver
{
    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly MutableTime time = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
    private readonly SparkConsentOptions options = new() { PublicOrigin = "https://shop.example" };
    private readonly IDataProtectionProvider dataProtection = new EphemeralDataProtectionProvider();

    private static readonly SparkConsentSubject Visitor = SparkConsentSubject.Visitor("0123456789abcdef0123456789abcdef");
    private static readonly SparkConsentSubject Account = SparkConsentSubject.Account("SparkUsers/1-A");

    private SparkConsentService Service(params SparkConsentPurpose[] extra)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        return new SparkConsentService(Store, Options.Create(options), Options.Create(new SparkVisitorOptions()),
            SparkBuilderConsentExtensions.BuiltInPurposes.Concat(extra), dataProtection, new HttpContextAccessor(), services);
    }

    private static SparkConsentChoice Choice(string purpose, bool granted, string source = SparkConsentSources.Banner)
        => new(new Dictionary<string, bool> { [purpose] = granted }, source, "en");

    [Fact]
    public async Task An_undecided_purpose_is_not_granted_and_necessary_always_is()
    {
        var consent = Service();

        (await consent.HasAsync(Visitor, SparkConsentPurposes.Analytics)).Should().BeFalse();
        (await consent.HasAsync(Visitor, SparkConsentPurposes.Necessary)).Should().BeTrue();
        (await consent.HasAsync(Visitor, "undeclared")).Should().BeFalse();
    }

    [Fact]
    public async Task A_grant_is_recorded_with_its_proof_and_a_refusal_withdraws_it()
    {
        var consent = Service();
        await consent.RecordAsync(Visitor, Choice(SparkConsentPurposes.Analytics, true));
        (await consent.HasAsync(Visitor, SparkConsentPurposes.Analytics)).Should().BeTrue();

        time.Now += TimeSpan.FromMinutes(1);
        await consent.RecordAsync(Visitor, Choice(SparkConsentPurposes.Analytics, false, SparkConsentSources.SettingsPage));
        (await consent.HasAsync(Visitor, SparkConsentPurposes.Analytics)).Should().BeFalse();

        using var session = Store.OpenAsyncSession();
        var record = await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(Visitor));
        record.History.Should().HaveCount(2);
        var first = record.History[0];
        first.Granted.Should().BeTrue();
        first.PolicyVersion.Should().Be("1");
        first.Source.Should().Be(SparkConsentSources.Banner);
        first.Language.Should().Be("en");
        first.TextHash.Should().HaveLength(64);
        // A guest's record expires with the cookie that can ask for it.
        session.Advanced.GetMetadataFor(record).ContainsKey("@expires").Should().BeTrue();
    }

    [Fact]
    public async Task A_new_policy_version_makes_decisions_stale_except_marketing_mail()
    {
        var consent = Service();
        await consent.RecordAsync(Account, new SparkConsentChoice(new()
        {
            [SparkConsentPurposes.Analytics] = true,
            [SparkConsentPurposes.MarketingMail] = true,
        }, SparkConsentSources.SettingsPage));

        options.PolicyVersion = "2";
        var fresh = Service();
        (await fresh.HasAsync(Account, SparkConsentPurposes.Analytics)).Should().BeFalse();
        (await fresh.HasAsync(Account, SparkConsentPurposes.MarketingMail)).Should().BeTrue();
    }

    [Fact]
    public async Task A_grant_expires_after_its_validity_and_the_banner_asks_again()
    {
        var consent = Service();
        await consent.RecordAsync(Visitor, Choice(SparkConsentPurposes.Analytics, true));

        time.Now += options.ConsentValidity + TimeSpan.FromDays(1);
        (await consent.HasAsync(Visitor, SparkConsentPurposes.Analytics)).Should().BeFalse();

        using var session = Store.OpenAsyncSession();
        var state = consent.StateOf(await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(Visitor)));
        state.NeedsPrompt.Should().BeTrue();
        (state.Purposes.Single(p => p.Name == SparkConsentPurposes.Analytics).Granted is null).Should().BeTrue();
    }

    [Fact]
    public async Task The_banner_shows_until_every_purpose_it_asks_about_is_decided()
    {
        var consent = Service();
        consent.StateOf(null).NeedsPrompt.Should().BeTrue();

        // Marketing mail is asked where it applies (checkout, settings), not by the banner.
        await consent.RecordAsync(Visitor, new SparkConsentChoice(new()
        {
            [SparkConsentPurposes.Analytics] = false,
            [SparkConsentPurposes.ThirdPartyChallenge] = false,
            [SparkConsentPurposes.ThirdPartyMedia] = true,
        }, SparkConsentSources.Banner));

        using var session = Store.OpenAsyncSession();
        consent.StateOf(await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(Visitor))).NeedsPrompt.Should().BeFalse();
    }

    [Fact]
    public async Task A_host_declaration_replaces_the_built_in_texts()
    {
        var own = new SparkConsentPurpose(SparkConsentPurposes.Analytics, TranslatedString.Of("en", "Matomo statistics"),
            TranslatedString.Of("en", "Self-hosted"), [new SparkConsentVendor("Matomo", "https://matomo.org/privacy/")]);
        var state = Service(own).StateOf(null);

        var analytics = state.Purposes.Single(p => p.Name == SparkConsentPurposes.Analytics);
        analytics.Title.GetValue("en").Should().Be("Matomo statistics");
        analytics.Vendors.Should().HaveCount(1);
        await Task.CompletedTask;
    }

    [Fact]
    public void Merging_on_sign_in_keeps_the_newer_decision_per_purpose()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        SparkConsentDecision Decision(string purpose, bool granted, int day) => new()
        {
            Purpose = purpose, Granted = granted, PolicyVersion = "1", AtUtc = at.AddDays(day), Source = SparkConsentSources.Banner,
        };

        var account = new SparkConsentRecord { SubjectKind = SparkConsentSubjectKind.Account, SubjectId = "SparkUsers/1-A" };
        account.Apply(Decision(SparkConsentPurposes.Analytics, true, 1), 100);
        account.Apply(Decision(SparkConsentPurposes.ThirdPartyMedia, true, 5), 100);

        var visitor = new SparkConsentRecord { SubjectKind = SparkConsentSubjectKind.Visitor, SubjectId = "v" };
        visitor.Apply(Decision(SparkConsentPurposes.Analytics, false, 3), 100);   // newer: wins
        visitor.Apply(Decision(SparkConsentPurposes.ThirdPartyMedia, false, 2), 100);   // older: history only

        SparkConsentVisitorMergeHandler.Merge(visitor, account, 100);

        account.Decisions[SparkConsentPurposes.Analytics].Granted.Should().BeFalse();
        account.Decisions[SparkConsentPurposes.ThirdPartyMedia].Granted.Should().BeTrue();
        account.History.Should().HaveCount(4);
        account.History.Select(h => h.AtUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task The_merge_handler_moves_the_visitors_record_onto_the_account_in_the_merge_session()
    {
        var consent = Service();
        await consent.RecordAsync(Visitor, Choice(SparkConsentPurposes.Analytics, true));

        using (var session = Store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide }))
        {
            await new SparkConsentVisitorMergeHandler(Options.Create(options)).MergeAsync(Visitor.Id, Account.Id, session, default);
            await session.SaveChangesAsync();
        }

        (await consent.HasAsync(Account, SparkConsentPurposes.Analytics)).Should().BeTrue();
        using var check = Store.OpenAsyncSession();
        (await check.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(Visitor))).Should().BeNull();
    }

    [Fact]
    public async Task An_unsubscribe_token_withdraws_its_purpose_and_a_tampered_one_does_nothing()
    {
        var consent = Service();
        var subscriber = SparkConsentSubject.Email(" Jane@Example.com ");
        await consent.RecordAsync(subscriber, Choice(SparkConsentPurposes.MarketingMail, true, SparkConsentSources.Checkout));

        var token = consent.CreateUnsubscribeToken(subscriber);
        (await consent.UnsubscribeAsync(token[..^2] + "AA", default)).Should().BeFalse();
        (await consent.UnsubscribeAsync("not-a-token", default)).Should().BeFalse();
        (await consent.HasAsync(subscriber, SparkConsentPurposes.MarketingMail)).Should().BeTrue();

        var info = await consent.DescribeUnsubscribeAsync(token, default);
        info!.Recipient.Should().Be("j***@example.com");
        info.Granted.Should().BeTrue();

        (await consent.UnsubscribeAsync(token, default)).Should().BeTrue();
        (await consent.HasAsync(SparkConsentSubject.Email("jane@example.com"), SparkConsentPurposes.MarketingMail)).Should().BeFalse();
        (await consent.UnsubscribeAsync(token, default)).Should().BeTrue(); // idempotent
    }

    [Fact]
    public void Marketing_mail_carries_one_click_list_unsubscribe_headers()
    {
        var headers = Service().CreateListUnsubscribeHeaders(SparkConsentSubject.Email("jane@example.com"));

        headers["List-Unsubscribe-Post"].Should().Be("List-Unsubscribe=One-Click");
        headers["List-Unsubscribe"].Should().StartWith("<https://shop.example/spark/consent/unsubscribe/one-click?token=");
        headers["List-Unsubscribe"].Should().EndWith(">");
        Service().CreateUnsubscribeUrl(SparkConsentSubject.Email("jane@example.com")).AbsoluteUri
            .Should().StartWith("https://shop.example/consent/unsubscribe?token=");
    }

    [Fact]
    public async Task The_email_record_id_never_holds_the_address()
    {
        SparkConsentRecord.IdFor(SparkConsentSubject.Email("jane@example.com")).Should().NotContain("jane");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Erasure_removes_the_accounts_and_its_addresss_records_and_export_shows_them_before()
    {
        var consent = Service();
        await consent.RecordAsync(Account, Choice(SparkConsentPurposes.Analytics, true));
        await consent.RecordAsync(SparkConsentSubject.Email("jane@example.com"), Choice(SparkConsentPurposes.MarketingMail, true, SparkConsentSources.Checkout));

        (await consent.ExportAsync(Account.Id, "jane@example.com", default)).Should().NotBeNull();

        await consent.EraseAsync(Account.Id, "jane@example.com", default);
        await consent.EraseAsync(Account.Id, "jane@example.com", default); // idempotent

        (await consent.ExportAsync(Account.Id, "jane@example.com", default)).Should().BeNull();
        (await consent.HasAsync(Account, SparkConsentPurposes.Analytics)).Should().BeFalse();
    }

    [Fact]
    public void Startup_refuses_a_requirement_without_the_primitive_and_one_whose_purpose_is_undeclared()
    {
        var unwired = new ServiceCollection().RequiresConsent(SparkConsentPurposes.ThirdPartyChallenge, "Challenges.Turnstile").BuildServiceProvider();
        var act = () => SparkConsentRegistration.ValidateWired(unwired);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Challenges.Turnstile needs 'third-party-challenge'*");

        var wired = new ServiceCollection()
            .AddScoped<ISparkConsent>(_ => Service())
            .AddConsentPurpose(SparkBuilderConsentExtensions.BuiltInPurposes[0])
            .RequiresConsent(SparkConsentPurposes.Analytics, "Analytics")
            .RequiresConsent("video", "Vimeo embed")
            .BuildServiceProvider();
        SparkConsentRegistration.FindUnmet(wired).Select(r => r.Purpose).Should().BeEquivalentTo(["video"]);
    }

    [Fact]
    public async Task The_gate_refuses_when_the_primitive_is_not_registered()
    {
        var none = new ServiceCollection().BuildServiceProvider();
        (await none.AllowsAsync(SparkConsentPurposes.ThirdPartyChallenge)).Should().BeFalse();
        (await none.AllowsAsync(SparkConsentPurposes.Necessary)).Should().BeTrue();
    }
}
