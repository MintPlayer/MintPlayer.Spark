using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Challenges;
using MintPlayer.Spark.Challenges.Altcha;
using MintPlayer.Spark.Challenges.Contracts;
using MintPlayer.Spark.Challenges.FriendlyCaptcha;
using MintPlayer.Spark.Challenges.HCaptcha;
using MintPlayer.Spark.Challenges.Recaptcha;
using MintPlayer.Spark.Challenges.Turnstile;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Commerce.Tests.WaitingRoom;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Challenges;

/// <summary>
/// The challenge providers (§4.12; S12 items S1 and S6 reduced per round 4: no device timings, no screen-reader runs):
/// ALTCHA's protocol, replay and expiry; each hosted adapter against fixtures taken from its vendor's public
/// documentation; the consent gate with the ALTCHA fallback (GA9); admission never challenged.
/// </summary>
public class ChallengeProviderTests : SparkTestDriver
{
    private readonly TestClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    private AltchaChallenge Altcha(long maxNumber = 1_000)
        => new(new EphemeralDataProtectionProvider(), new RiskPipelineTests.StaticMonitor(new SparkChallengesOptions { Altcha = { MaxNumber = maxNumber } }), clock);

    // ------------------------------------------------------------------ ALTCHA

    [Fact]
    public async Task An_altcha_solution_verifies_once_for_its_point_and_not_after_expiry()
    {
        var altcha = Altcha();
        var offer = await altcha.OfferAsync(new SparkChallengeRequest(SparkChallengePoints.QueueJoin), default);
        offer.Altcha!.Algorithm.Should().Be("SHA-256");
        offer.Altcha.Maxnumber.Should().Be(1_000);
        offer.Altcha.Salt.Should().Contain("?expires=");
        var solution = ChallengeTestSupport.Solve(offer.Altcha);

        altcha.Verify(SparkChallengePoints.QueueJoin, solution).Passed.Should().BeTrue();
        altcha.Verify(SparkChallengePoints.Hold, solution).Error.Should().Be("invalid"); // sealed for another point
        altcha.Verify(SparkChallengePoints.QueueJoin, ChallengeTestSupport.Solve(offer.Altcha, signature: "forged")).Passed.Should().BeFalse();
        altcha.Verify(SparkChallengePoints.QueueJoin, "not base64 json").Error.Should().Be("invalid");

        clock.Advance(TimeSpan.FromMinutes(11));
        altcha.Verify(SparkChallengePoints.QueueJoin, solution).Error.Should().Be("expired");
    }

    [Fact]
    public async Task Changing_the_salt_to_extend_the_expiry_breaks_the_hash()
    {
        var altcha = Altcha();
        var offer = (await altcha.OfferAsync(new SparkChallengeRequest(SparkChallengePoints.QueueJoin), default)).Altcha!;
        var later = offer.Salt[..offer.Salt.IndexOf('?')] + "?expires=" + clock.GetUtcNow().AddYears(1).ToUnixTimeSeconds();
        altcha.Verify(SparkChallengePoints.QueueJoin, ChallengeTestSupport.Solve(offer, salt: later)).Error.Should().Be("invalid");
    }

    [Fact]
    public void Altcha_hashes_like_altcha_lib()
    {
        // altcha-lib v1: challenge = hex(SHA-256(salt + number)). Known vector computed independently: SHA-256("abc123").
        AltchaChallenge.Hash("abc", 123).Should().Be("6ca13d52ca70c883e0f0bb101e425a89e8624de51db2d2392593af6a84118090");
    }

    // ------------------------------------------------------------------ the service: replay, consent fallback

    [Fact]
    public async Task A_verified_response_is_refused_as_a_replay()
    {
        await using var services = Services(consent: null, hosted: false);
        var challenges = services.GetRequiredService<ISparkHumanChallenges>();
        var offer = await challenges.OfferAsync(SparkChallengePoints.QueueJoin);
        var response = new SparkChallengeResponse("altcha", ChallengeTestSupport.Solve(offer.Altcha!));

        (await challenges.VerifyAsync(SparkChallengePoints.QueueJoin, response)).Passed.Should().BeTrue();
        (await challenges.VerifyAsync(SparkChallengePoints.QueueJoin, response)).Error.Should().Be("replayed");
    }

    [Fact]
    public async Task Without_consent_a_hosted_provider_is_replaced_by_altcha_and_its_tokens_are_refused()
    {
        await using var refused = Services(consent: false, hosted: true);
        var challenges = refused.GetRequiredService<ISparkHumanChallenges>();
        var offer = await challenges.OfferAsync(SparkChallengePoints.QueueJoin);
        offer.Provider.Should().Be("altcha");
        offer.ConsentFallback.Should().BeTrue();
        (await challenges.VerifyAsync(SparkChallengePoints.QueueJoin, new SparkChallengeResponse("turnstile", "XXXX.DUMMY.TOKEN.XXXX"))).Error.Should().Be("consent");

        await using var granted = Services(consent: true, hosted: true);
        var withConsent = granted.GetRequiredService<ISparkHumanChallenges>();
        (await withConsent.OfferAsync(SparkChallengePoints.QueueJoin)).Provider.Should().Be("turnstile");
        (await withConsent.VerifyAsync(SparkChallengePoints.QueueJoin, new SparkChallengeResponse("turnstile", "XXXX.DUMMY.TOKEN.XXXX"))).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Admission_is_never_challenged_and_points_switch_at_runtime()
    {
        await using var services = Services(consent: null, hosted: false);
        var challenges = services.GetRequiredService<ISparkHumanChallenges>();
        var admission = () => challenges.OfferAsync(SparkChallengePoints.Admission);
        await admission.Should().ThrowAsync<ArgumentException>();

        (await challenges.IsEnabledAtAsync(SparkChallengePoints.QueueJoin)).Should().BeFalse();
        services.GetRequiredService<IOptionsMonitor<SparkChallengesOptions>>().CurrentValue.Points.Add(SparkChallengePoints.QueueJoin);
        (await challenges.IsEnabledAtAsync(SparkChallengePoints.QueueJoin)).Should().BeTrue();
    }

    [Fact]
    public void A_hosted_provider_declares_its_consent_requirement()
    {
        var services = new ServiceCollection();
        var registry = new SparkChallengeProviderRegistry();
        new SparkChallengesBuilder(services, registry).UseTurnstile();
        services.Any(d => d.ServiceType == typeof(SparkConsentRequirement)
            && ((SparkConsentRequirement)d.ImplementationInstance!).Purpose == SparkConsentPurposes.ThirdPartyChallenge).Should().BeTrue();
        services.Any(d => d.ImplementationType == typeof(AltchaChallenge)).Should().BeTrue();
        registry.Order.Should().Equal("altcha", "turnstile");
    }

    // ------------------------------------------------------------------ hosted adapters, public-documentation fixtures

    // Fixtures from the vendors' public documentation (round 4, D37), not recordings:
    // Turnstile: developers.cloudflare.com/turnstile/get-started/server-side-validation/ (success and error responses).
    // hCaptcha: docs.hcaptcha.com/#verify-the-user-response-server-side.
    // reCAPTCHA: developers.google.com/recaptcha/docs/v3#site_verify_response.
    // Friendly Captcha: developer.friendlycaptcha.com/docs/v2/api/siteverify.
    private const string TurnstileSuccess = """{"success":true,"challenge_ts":"2022-02-28T15:14:30.096Z","hostname":"example.com","error-codes":[],"action":"queue-join","cdata":"sessionid-123456789"}""";
    private const string TurnstileFailure = """{"success":false,"error-codes":["invalid-input-response"]}""";
    private const string HCaptchaSuccess = """{"success":true,"challenge_ts":"2026-10-19T12:00:00Z","hostname":"example.com","credit":false,"error-codes":[],"score":0.2,"score_reason":["safe"]}""";
    private const string RecaptchaLowScore = """{"success":true,"score":0.3,"action":"queue_join","challenge_ts":"2026-10-19T12:00:00Z","hostname":"example.com","error-codes":[]}""";
    private const string RecaptchaGood = """{"success":true,"score":0.9,"action":"queue_join","challenge_ts":"2026-10-19T12:00:00Z","hostname":"example.com","error-codes":[]}""";
    private const string FriendlyFailure = """{"success":false,"error":{"error_code":"response_timeout","detail":"The response has expired."}}""";
    private const string FriendlySuccess = """{"success":true,"data":{"event_id":"ev_1234","challenge":{"timestamp":"2026-10-19T12:00:00Z","origin":"https://example.com"}}}""";

    [Fact]
    public async Task Turnstile_posts_the_documented_form_and_reads_the_documented_answer()
    {
        var handler = new FixtureHandler(TurnstileSuccess);
        var turnstile = new TurnstileChallenge(handler.Factory(), Monitor(new TurnstileOptions { SiteKey = "1x00000000000000000000AA", SecretKey = "test-secret", ExpectedHostname = "example.com" }), NullLogger<TurnstileChallenge>.Instance);
        var request = new SparkChallengeRequest(SparkChallengePoints.QueueJoin, RemoteAddress: "203.0.113.7");

        (await turnstile.VerifyAsync(request, "token-1", default)).Passed.Should().BeTrue();
        handler.LastUrl.Should().Be("https://challenges.cloudflare.com/turnstile/v0/siteverify");
        handler.LastBody.Should().Be("secret=test-secret&response=token-1&remoteip=203.0.113.7");

        handler.Answer = TurnstileFailure;
        (await turnstile.VerifyAsync(request, "token-2", default)).Error.Should().Be("invalid-input-response");
        handler.Answer = TurnstileSuccess.Replace("example.com", "evil.example");
        (await turnstile.VerifyAsync(request, "token-3", default)).Error.Should().Be("hostname-mismatch");
        (await turnstile.VerifyAsync(request with { Point = SparkChallengePoints.Hold }, "token-4", default)).Passed.Should().BeFalse();
        handler.Fail = true;
        (await turnstile.VerifyAsync(request, "token-5", default)).Error.Should().Be("unavailable");
    }

    [Fact]
    public async Task HCaptcha_sends_the_sitekey_and_honours_an_enterprise_score_ceiling()
    {
        var handler = new FixtureHandler(HCaptchaSuccess);
        var options = new HCaptchaOptions { SiteKey = "10000000-ffff-ffff-ffff-000000000001", Secret = "0x0000000000000000000000000000000000000000" };
        var hcaptcha = new HCaptchaChallenge(handler.Factory(), Monitor(options), NullLogger<HCaptchaChallenge>.Instance);
        var request = new SparkChallengeRequest(SparkChallengePoints.QueueJoin);

        var result = await hcaptcha.VerifyAsync(request, "token", default);
        result.Passed.Should().BeTrue();
        result.Score.Should().Be(0.8);
        handler.LastBody.Should().Contain("sitekey=10000000-ffff-ffff-ffff-000000000001");
        options.MaximumScore = 0.1;
        (await hcaptcha.VerifyAsync(request, "token", default)).Error.Should().Be("score");
    }

    [Fact]
    public async Task Recaptcha_v3_needs_the_minimum_score_and_the_action()
    {
        var handler = new FixtureHandler(RecaptchaLowScore);
        var recaptcha = new RecaptchaChallenge(handler.Factory(), Monitor(new RecaptchaOptions { SiteKey = "site", SecretKey = "secret" }), NullLogger<RecaptchaChallenge>.Instance);
        var request = new SparkChallengeRequest(SparkChallengePoints.QueueJoin);

        (await recaptcha.VerifyAsync(request, "token", default)).Error.Should().Be("score");
        handler.Answer = RecaptchaGood;
        (await recaptcha.VerifyAsync(request, "token", default)).Passed.Should().BeTrue();
        (await recaptcha.VerifyAsync(request with { Point = SparkChallengePoints.Hold }, "token", default)).Error.Should().Be("action-mismatch");
        (await recaptcha.OfferAsync(request, default)).ScriptUrl.Should().Be("https://www.google.com/recaptcha/api.js?render=site");
    }

    [Fact]
    public async Task Friendly_captcha_posts_json_with_the_api_key_header_on_the_chosen_endpoint()
    {
        var handler = new FixtureHandler(FriendlyFailure);
        var friendly = new FriendlyCaptchaChallenge(handler.Factory(), Monitor(new FriendlyCaptchaOptions { SiteKey = "FCMG", ApiKey = "api-key", UseEuEndpoint = true }), NullLogger<FriendlyCaptchaChallenge>.Instance);
        var request = new SparkChallengeRequest(SparkChallengePoints.QueueJoin);

        (await friendly.VerifyAsync(request, "solution", default)).Error.Should().Be("response_timeout");
        handler.LastUrl.Should().Be("https://eu.frcapi.com/api/v2/captcha/siteverify");
        handler.LastApiKey.Should().Be("api-key");
        handler.LastBody.Should().Be("""{"response":"solution","sitekey":"FCMG"}""");
        handler.Answer = FriendlySuccess;
        (await friendly.VerifyAsync(request, "solution", default)).Passed.Should().BeTrue();
    }

    // ------------------------------------------------------------------ helpers

    private ServiceProvider Services(bool? consent, bool hosted)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddSingleton<ISparkClaims>(new SparkClaims(Store, clock));
        services.Configure<SparkChallengesOptions>(o => o.Altcha.MaxNumber = 1_000);
        var registry = new SparkChallengeProviderRegistry();
        services.AddSingleton(registry);
        services.AddSingleton<ISparkChallengeSwitch, OptionsChallengeSwitch>();
        services.AddScoped<ISparkHumanChallenges, SparkHumanChallenges>();
        var builder = new SparkChallengesBuilder(services, registry).UseAltcha();
        if (hosted)
        {
            builder.UseTurnstile(o => { o.SiteKey = "1x00000000000000000000AA"; o.SecretKey = "test-secret"; });
            services.AddSingleton<IHttpClientFactory>(new FixtureHandler(TurnstileSuccess).Factory());
        }
        if (consent is { } granted)
            services.AddSingleton<ISparkConsent>(new FixedConsent(granted));
        return services.BuildServiceProvider();
    }

    private static IOptionsMonitor<T> Monitor<T>(T value) where T : class => new FixedMonitor<T>(value);

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedConsent(bool granted) : ISparkConsent
    {
        public Task<bool> HasAsync(string purpose, CancellationToken cancellationToken = default) => Task.FromResult(granted);
        public Task<bool> HasAsync(SparkConsentSubject subject, string purpose, CancellationToken cancellationToken = default) => Task.FromResult(granted);
        public Task<SparkConsentState> GetStateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SparkConsentState> RecordAsync(SparkConsentChoice choice, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RecordAsync(SparkConsentSubject subject, SparkConsentChoice choice, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Answers every request with a fixture and remembers what was sent.</summary>
    private sealed class FixtureHandler(string answer) : HttpMessageHandler
    {
        public string Answer { get; set; } = answer;
        public bool Fail { get; set; }
        public string? LastUrl { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastApiKey { get; private set; }

        public IHttpClientFactory Factory() => new SingleFactory(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new HttpRequestException("unreachable");
            LastUrl = request.RequestUri!.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastApiKey = request.Headers.TryGetValues("X-API-Key", out var values) ? values.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };
        }

        private sealed class SingleFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }
}
