using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.OneShotOAuth;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization.OneShotOAuth;

/// <summary>
/// The one-shot OAuth token (#554 f1): authorized in a popup through a client-method retry, exchanged
/// by the callback with PKCE, held in memory for one user and five minutes, taken once, revoked when
/// the lease is disposed.
/// </summary>
/// <remarks>
/// Each pass is a fresh request scope (a new retry accessor and service), as in production: the action
/// re-runs from the top on pass 2. The store and the clock are the singletons that outlive the passes.
/// </remarks>
public class SparkOneShotOAuthTests
{
    private const string ClientName = "rulesets";
    private const string Origin = "https://coverage.example";

    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider dataProtection = new EphemeralDataProtectionProvider();
    private readonly SparkOneShotOAuthStore store;
    private readonly ProviderStub provider = new();
    private readonly List<string> revoked = [];
    private readonly SparkOneShotOAuthOptions options = new();
    private ClaimsPrincipal principal = SignedIn("users/alice");
    private ClientAccessor client = new();
    private readonly IHttpClientFactory httpClients;

    public SparkOneShotOAuthTests()
    {
        var httpClients = Substitute.For<IHttpClientFactory>();
        httpClients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(provider, disposeHandler: false));
        this.httpClients = httpClients;
        store = new SparkOneShotOAuthStore(clock, httpClients, Options.Create(options), NullLogger<SparkOneShotOAuthStore>.Instance);
        options.Clients[ClientName] = new SparkOneShotOAuthClient
        {
            AuthorizationEndpoint = "https://provider.example/authorize",
            TokenEndpoint = "https://provider.example/token",
            ClientId = "client-id",
            ClientSecret = "client-secret",
            Revoke = (revocation, _) =>
            {
                revoked.Add(revocation.AccessToken);
                return Task.CompletedTask;
            },
        };
    }

    #region Fixture

    private static ClaimsPrincipal SignedIn(string userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestCookie"));

    private IServiceProvider Scope(params RetryResult[] answered)
    {
        client = new ClientAccessor();
        var retry = new RetryAccessor(client)
        {
            AnsweredResults = answered.Length == 0 ? null : answered.ToDictionary(r => r.Step),
        };
        var manager = Substitute.For<IManager>();
        manager.Retry.Returns(retry);
        manager.Client.Returns(client);

        var context = new DefaultHttpContext { User = principal };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("coverage.example");

        return new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context })
            .AddSingleton(manager)
            .AddSingleton(store)
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(dataProtection)
            .AddSingleton(httpClients)
            .AddSingleton(Options.Create(options))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddScoped<ISparkOneShotOAuth, SparkOneShotOAuth>()
            .AddScoped<SparkOneShotOAuthCallback>()
            .BuildServiceProvider()
            .CreateScope().ServiceProvider;
    }

    private Task<SparkOneShotOAuthOutcome> Authorize(params RetryResult[] answered)
        => Scope(answered).GetRequiredService<ISparkOneShotOAuth>().AuthorizeAsync(ClientName, ["repo"]);

    /// <summary>Pass 1: returns the client method's arguments and the authorization URL's query.</summary>
    private async Task<(JsonElement Args, Dictionary<string, string> Query)> PassOne()
    {
        var act = () => Authorize();
        await act.Should().ThrowAsync<SparkRetryActionException>();
        var op = client.Operations.OfType<RetryOperation>().Should().ContainSingle().Which;
        op.ClientMethod.Should().Be("oauth.authorize");
        var args = op.Arguments!.Value;
        var url = new Uri(args.GetProperty("url").GetString()!);
        var query = QueryHelpers.ParseQuery(url.Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        return (args, query);
    }

    private Task<SparkOneShotOAuthCallbackResult> Callback(string? state, string? code = "the-code", string? error = null)
        => Scope().GetRequiredService<SparkOneShotOAuthCallback>().CompleteAsync(state, code, error);

    private static RetryResult Answer(string? valueJson, string option = "OK") => new()
    {
        Step = 0,
        Option = option,
        Value = valueJson is null ? null : JsonDocument.Parse(valueJson).RootElement.Clone(),
    };

    private static RetryResult Handed(JsonElement args)
        => Answer(JsonSerializer.Serialize(new { handle = args.GetProperty("handle").GetString() }));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    #endregion

    #region Pass 1: the authorization URL

    [Fact]
    public async Task Pass_one_hands_the_browser_an_authorization_url_with_pkce_s256_and_a_state()
    {
        var (args, query) = await PassOne();

        args.GetProperty("url").GetString().Should().StartWith("https://provider.example/authorize?");
        query["response_type"].Should().Be("code");
        query["client_id"].Should().Be("client-id");
        query["redirect_uri"].Should().Be($"{Origin}/spark/auth/oauth-callback");
        query["scope"].Should().Be("repo");
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        query["state"].Should().NotBeNullOrEmpty();
        query["state"].Should().NotBe(args.GetProperty("handle").GetString(), "the state is the protected handle, not the handle itself");
        args.GetProperty("nonce").GetString().Should().MatchRegex("^[A-Za-z0-9_-]{16,64}$", "the popup page's nonce rules apply");
        provider.TokenRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unregistered_client_is_a_wiring_mistake()
    {
        var act = () => Scope().GetRequiredService<ISparkOneShotOAuth>().AuthorizeAsync("nope", ["repo"]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nope*");
    }

    [Fact]
    public async Task An_anonymous_caller_gets_no_authorization_url()
    {
        principal = new ClaimsPrincipal(new ClaimsIdentity());

        var outcome = await Authorize();

        outcome.Error.Should().Be(SparkOneShotOAuthErrors.Failed);
        client.Operations.OfType<RetryOperation>().Should().BeEmpty();
    }

    #endregion

    #region The callback and pass 2

    [Fact]
    public async Task The_round_trip_exchanges_the_code_with_the_verifier_and_hands_out_the_token_once()
    {
        var (args, query) = await PassOne();

        var page = await Callback(query["state"]);
        page.Nonce.Should().Be(args.GetProperty("nonce").GetString());
        page.Error.Should().BeNull();

        var exchange = provider.TokenRequests.Should().ContainSingle().Which;
        exchange["grant_type"].Should().Be("authorization_code");
        exchange["code"].Should().Be("the-code");
        exchange["client_id"].Should().Be("client-id");
        exchange["client_secret"].Should().Be("client-secret");
        exchange["redirect_uri"].Should().Be($"{Origin}/spark/auth/oauth-callback");
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"])))
            .Should().Be(query["code_challenge"], "the verifier must match the challenge sent on pass 1");

        var outcome = await Authorize(Handed(args));
        outcome.Succeeded.Should().BeTrue();
        outcome.Lease!.AccessToken.Should().Be(ProviderStub.Token);
        outcome.Lease.GrantedScope.Should().Be("repo");

        var concurrent = await Authorize(Handed(args));
        concurrent.Error.Should().Be(SparkOneShotOAuthErrors.Failed, "one pass holds the token at a time");

        await outcome.Lease.DisposeAsync();
        var after = await Authorize(Handed(args));
        after.Error.Should().Be(SparkOneShotOAuthErrors.Expired, "a finished action spends the token");
    }

    [Fact]
    public async Task Pass_two_does_not_build_a_second_authorization_url()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);

        await Authorize(Handed(args));

        client.Operations.OfType<RetryOperation>().Should().BeEmpty("the factory runs only for an unanswered step");
    }

    [Fact]
    public async Task Another_users_callback_is_refused_without_an_exchange()
    {
        var (_, query) = await PassOne();
        principal = SignedIn("users/mallory");

        var page = await Callback(query["state"]);

        page.Error.Should().Be(SparkOneShotOAuthErrors.Failed);
        provider.TokenRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_user_cannot_take_the_token()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);
        principal = SignedIn("users/mallory");

        var stolen = await Authorize(Handed(args));
        stolen.Error.Should().Be(SparkOneShotOAuthErrors.Failed);

        principal = SignedIn("users/alice");
        var outcome = await Authorize(Handed(args));
        outcome.Error.Should().Be(SparkOneShotOAuthErrors.Expired, "a refused take still spends the handle");
        revoked.Should().Equal([ProviderStub.Token], "a token nobody may take is revoked at once");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-protected-state")]
    public async Task A_missing_or_tampered_state_is_refused_without_an_exchange(string? state)
    {
        await PassOne();

        var page = await Callback(state);

        page.Error.Should().Be(SparkOneShotOAuthErrors.Failed);
        page.Nonce.Should().BeNull("an unknown state names no attempt to report to");
        provider.TokenRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_consent_reaches_pass_two_as_denied()
    {
        var (args, query) = await PassOne();

        var page = await Callback(query["state"], code: null, error: "access_denied");

        page.Error.Should().Be(SparkOneShotOAuthErrors.Denied);
        provider.TokenRequests.Should().BeEmpty();
        (await Authorize(Handed(args))).Error.Should().Be(SparkOneShotOAuthErrors.Denied);
    }

    [Fact]
    public async Task A_failed_exchange_reaches_pass_two_as_failed()
    {
        provider.TokenResponse = """{"error":"bad_verification_code"}""";
        var (args, query) = await PassOne();

        var page = await Callback(query["state"]);

        page.Error.Should().Be(SparkOneShotOAuthErrors.Failed);
        (await Authorize(Handed(args))).Error.Should().Be(SparkOneShotOAuthErrors.Failed);
    }

    [Fact]
    public async Task A_callback_after_five_minutes_is_expired()
    {
        var (_, query) = await PassOne();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var page = await Callback(query["state"]);

        page.Error.Should().Be(SparkOneShotOAuthErrors.Expired);
        provider.TokenRequests.Should().BeEmpty();
    }

    /// <summary>
    /// ⚠️ An OAuth app token does not expire on its own. A user who authorizes and then closes the tab
    /// never sends pass 2, so the store revokes the token itself when its five minutes end.
    /// </summary>
    [Fact]
    public async Task A_token_nobody_takes_is_revoked_when_its_five_minutes_end()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);

        clock.Advance(TimeSpan.FromMinutes(4));
        revoked.Should().BeEmpty();
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await store.Idle();

        revoked.Should().Equal(ProviderStub.Token);
        (await Authorize(Handed(args))).Error.Should().Be(SparkOneShotOAuthErrors.Expired);
        revoked.Should().ContainSingle("the take found nothing left to revoke");
    }

    [Fact]
    public async Task A_token_past_its_five_minutes_is_not_handed_out_even_before_the_timer_runs()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);
        clock.Freeze();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var outcome = await Authorize(Handed(args));

        outcome.Error.Should().Be(SparkOneShotOAuthErrors.Expired);
        revoked.Should().Equal(ProviderStub.Token);
    }

    [Fact]
    public async Task A_second_callback_for_the_same_state_is_refused()
    {
        var (_, query) = await PassOne();
        await Callback(query["state"]);

        var replay = await Callback(query["state"], code: "another-code");

        replay.Error.Should().Be(SparkOneShotOAuthErrors.Failed);
        provider.TokenRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_cancelled_step_is_cancelled()
    {
        (await Authorize(Answer(null, RetryResult.CancelOption))).Error.Should().Be(SparkOneShotOAuthErrors.Cancelled);
    }

    [Fact]
    public async Task A_blocked_popup_says_so()
    {
        (await Authorize(Answer("""{"error":"popup_blocked"}"""))).Error.Should().Be(SparkOneShotOAuthErrors.PopupBlocked);
    }

    [Fact]
    public async Task An_unknown_handle_is_expired()
    {
        (await Authorize(Answer("""{"handle":"made-up"}"""))).Error.Should().Be(SparkOneShotOAuthErrors.Expired);
    }

    #endregion

    #region The lease

    [Fact]
    public async Task Disposing_the_lease_revokes_the_token_once()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);
        var lease = (await Authorize(Handed(args))).Lease!;

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        revoked.Should().Equal(ProviderStub.Token);
    }

    [Fact]
    public async Task The_token_is_revoked_when_the_work_throws()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);

        var work = async () =>
        {
            await using var lease = (await Authorize(Handed(args))).Lease!;
            throw new InvalidOperationException("the work failed");
        };

        await work.Should().ThrowAsync<InvalidOperationException>().WithMessage("the work failed");
        revoked.Should().Equal(ProviderStub.Token);
    }

    /// <summary>One pass of an action that asks a prompt after authorizing: the prompt unwinds the pass.</summary>
    private async Task PassWithPrompt(params RetryResult[] answered)
    {
        var services = Scope(answered);
        var work = async () =>
        {
            await using var lease = (await services.GetRequiredService<ISparkOneShotOAuth>().AuthorizeAsync(ClientName, ["repo"])).Lease!;
            services.GetRequiredService<IManager>().Retry.Action("Confirm?", ["Apply"]);
        };
        await work.Should().ThrowAsync<SparkRetryActionException>();
    }

    /// <summary>#554 f3: the token is read with, a confirmation shown, then the token applies: one popup.</summary>
    [Fact]
    public async Task A_prompt_after_authorizing_keeps_the_token_for_the_next_pass()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);

        await PassWithPrompt(Handed(args));
        revoked.Should().BeEmpty("the pass only waits for the confirmation");

        var confirmed = new RetryResult { Step = 1, Option = "Apply" };
        var services = Scope(Handed(args), confirmed);
        var outcome = await services.GetRequiredService<ISparkOneShotOAuth>().AuthorizeAsync(ClientName, ["repo"]);
        outcome.Lease!.AccessToken.Should().Be(ProviderStub.Token);
        provider.TokenRequests.Should().ContainSingle("no second exchange");

        await outcome.Lease.DisposeAsync();
        revoked.Should().Equal(ProviderStub.Token);
        (await Authorize(Handed(args))).Error.Should().Be(SparkOneShotOAuthErrors.Expired);
    }

    [Fact]
    public async Task A_conversation_left_at_its_prompt_is_revoked_when_its_five_minutes_end()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);
        await PassWithPrompt(Handed(args));

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await store.Idle();

        revoked.Should().Equal(ProviderStub.Token);
        (await Authorize(Handed(args))).Error.Should().Be(SparkOneShotOAuthErrors.Expired);
    }

    [Fact]
    public async Task A_lease_disposed_after_its_five_minutes_does_not_revoke_twice()
    {
        var (args, query) = await PassOne();
        await Callback(query["state"]);
        var lease = (await Authorize(Handed(args))).Lease!;

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await store.Idle();
        await lease.DisposeAsync();

        revoked.Should().Equal(ProviderStub.Token);
    }

    [Fact]
    public async Task A_failing_revocation_does_not_replace_the_works_outcome()
    {
        options.Clients[ClientName].Revoke = (_, _) => throw new HttpRequestException("provider down");
        var (args, query) = await PassOne();
        await Callback(query["state"]);
        var lease = (await Authorize(Handed(args))).Lease!;

        var dispose = async () => await lease.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }

    #endregion

    private sealed class ProviderStub : HttpMessageHandler
    {
        public const string Token = "gho_test-token";

        public string TokenResponse { get; set; } = $$"""{"access_token":"{{Token}}","token_type":"bearer","scope":"repo"}""";

        public List<Dictionary<string, string>> TokenRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.ToString().Should().Be("https://provider.example/token");
            request.Method.Should().Be(HttpMethod.Post);
            request.Headers.Accept.Should().Contain(h => h.MediaType == "application/json");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            TokenRequests.Add(QueryHelpers.ParseQuery(body).ToDictionary(p => p.Key, p => p.Value.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TokenResponse, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>A clock whose one-shot timers fire when <see cref="Advance"/> passes their due time.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = start;
        private bool frozen;

        public override DateTimeOffset GetUtcNow() => now;

        /// <summary>Timers stop firing: time passes without the store hearing of it.</summary>
        public void Freeze() => frozen = true;

        public void Advance(TimeSpan by)
        {
            now += by;
            if (frozen) return;
            foreach (var timer in timers.Where(t => t.Due is { } due && due <= now).ToList())
                timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                return true;
            }

            public void Fire()
            {
                Due = null;
                callback(state);
            }

            public void Dispose() => Due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
