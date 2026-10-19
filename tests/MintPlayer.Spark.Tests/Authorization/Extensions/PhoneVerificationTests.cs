using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Sms;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Phone verification (<c>docs/commerce_PRD.md</c> X13, D29) through the real <c>/spark/auth/manage/phone*</c>
/// routes, with the recording SMS adapter: E.164 normalization, hashed single-use codes, expiry, the attempt
/// cap, the send limits per account and per number, and one verified account per number.
/// </summary>
public class PhoneVerificationTests : SparkTestDriver
{
    private const string Number = "+32470123456";

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock clock = new(DateTimeOffset.UtcNow);

    private Task<AccountTestHost> StartAsync(Action<SparkPhoneVerificationOptions>? configure = null, bool withPhoneVerification = true)
        => AccountTestHost.StartAsync(Store, services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSparkTestSms();
            if (withPhoneVerification)
                SparkPhoneVerificationExtensions.AddPhoneVerificationServices(s, () => typeof(SparkUser), configure);
        });

    private static SparkTestSmsSender Sms(AccountTestHost host) => host.Host.Services.GetRequiredService<SparkTestSmsSender>();

    private static string CodeIn(SparkSmsMessage message) => Regex.Match(message.Body, @"\b\d{6}\b").Value;

    private static Task<HttpResponseMessage> Send(HttpClient client, string cookie, string phoneNumber)
        => AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/phone/send", cookie, new { phoneNumber });

    private static Task<HttpResponseMessage> Verify(HttpClient client, string cookie, string code)
        => AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/phone/verify", cookie, new { code });

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(SparkUser User, string Cookie)> SignedInAsync(AccountTestHost host, HttpClient client, string name)
    {
        var user = await host.CreateUserAsync(name, name + "@example.com");
        return (user, await host.CookieSignInAsync(client, name + "@example.com"));
    }

    private async Task<SparkPhoneVerificationState?> StateAsync(string userId)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<SparkPhoneVerificationState>(SparkPhoneVerificationState.IdFor(userId));
    }

    [Fact]
    public async Task A_code_sent_by_SMS_verifies_the_number_once_and_only_its_hash_is_stored()
    {
        await using var host = await StartAsync();
        using var client = host.Client();
        var (user, cookie) = await SignedInAsync(host, client, "phoebe");

        var sent = await Send(client, cookie, "+32 470 12 34 56");

        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(sent)).GetProperty("outcome").GetString().Should().Be("codeSent");
        var message = Sms(host).LastTo(Number);
        message.Should().NotBeNull("the number is sent to in E.164 form");
        message!.Purpose.Should().Be("phone-verification");
        var code = CodeIn(message);
        code.Should().HaveLength(6);

        var state = await StateAsync(user.Id!);
        state!.CodeHash.Should().NotBeNullOrEmpty();
        state.CodeHash.Should().NotContain(code);
        JsonSerializer.Serialize(state).Should().NotContain(code, "only an HMAC of the code is stored");

        var wrong = await Verify(client, cookie, code == "000000" ? "111111" : "000000");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(wrong)).GetProperty("attemptsLeft").GetInt32().Should().Be(4);
        (await host.FindByEmailAsync("phoebe@example.com"))!.PhoneNumberConfirmed.Should().BeFalse();

        var right = await Verify(client, cookie, code[..3] + " " + code[3..]);
        right.StatusCode.Should().Be(HttpStatusCode.OK);
        var verified = (await host.FindByEmailAsync("phoebe@example.com"))!;
        verified.PhoneNumber.Should().Be(Number);
        verified.PhoneNumberConfirmed.Should().BeTrue();

        var replay = await Verify(client, cookie, code);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a code is single-use");
        (await Json(replay)).GetProperty("outcome").GetString().Should().Be("noPendingCode");
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        await using var host = await StartAsync(o => o.CodeLifetime = TimeSpan.FromMinutes(5));
        using var client = host.Client();
        var (_, cookie) = await SignedInAsync(host, client, "eve");

        (await Send(client, cookie, Number)).StatusCode.Should().Be(HttpStatusCode.OK);
        var code = CodeIn(Sms(host).LastTo(Number)!);
        clock.Now += TimeSpan.FromMinutes(5);

        var late = await Verify(client, cookie, code);

        late.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(late)).GetProperty("outcome").GetString().Should().Be("expired");
        (await host.FindByEmailAsync("eve@example.com"))!.PhoneNumberConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task The_last_allowed_wrong_guess_voids_the_code()
    {
        await using var host = await StartAsync(o => o.MaxAttemptsPerCode = 2);
        using var client = host.Client();
        var (_, cookie) = await SignedInAsync(host, client, "guesser");

        (await Send(client, cookie, Number)).StatusCode.Should().Be(HttpStatusCode.OK);
        var code = CodeIn(Sms(host).LastTo(Number)!);
        var wrong = code == "000000" ? "111111" : "000000";

        (await Json(await Verify(client, cookie, wrong))).GetProperty("outcome").GetString().Should().Be("wrongCode");
        (await Json(await Verify(client, cookie, wrong))).GetProperty("outcome").GetString().Should().Be("tooManyAttempts");
        var afterVoid = await Verify(client, cookie, code);

        afterVoid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the right code no longer counts once the attempts ran out");
        (await Json(afterVoid)).GetProperty("outcome").GetString().Should().Be("noPendingCode");
    }

    [Fact]
    public async Task Sends_are_limited_per_account_by_interval_and_by_count()
    {
        await using var host = await StartAsync(o =>
        {
            o.ResendInterval = TimeSpan.FromSeconds(60);
            o.MaxSendsPerUser = 2;
            o.MaxSendsPerNumber = 10;
        });
        using var client = host.Client();
        var (_, cookie) = await SignedInAsync(host, client, "spammer");

        (await Send(client, cookie, Number)).StatusCode.Should().Be(HttpStatusCode.OK);
        var tooSoon = await Send(client, cookie, Number);
        tooSoon.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        tooSoon.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));

        clock.Now += TimeSpan.FromSeconds(61);
        (await Send(client, cookie, "+32470123457")).StatusCode.Should().Be(HttpStatusCode.OK);
        clock.Now += TimeSpan.FromSeconds(61);
        (await Send(client, cookie, "+32470123458")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "two sends per window, to any numbers");

        clock.Now += TimeSpan.FromHours(24);
        (await Send(client, cookie, Number)).StatusCode.Should().Be(HttpStatusCode.OK, "the window has passed");
        Sms(host).Sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task Sends_are_limited_per_number_across_accounts()
    {
        await using var host = await StartAsync(o =>
        {
            o.MaxSendsPerNumber = 2;
            o.MaxSendsPerUser = 10;
        });
        using var client = host.Client();
        var (_, a) = await SignedInAsync(host, client, "first");
        var (_, b) = await SignedInAsync(host, client, "second");
        var (_, c) = await SignedInAsync(host, client, "third");

        (await Send(client, a, Number)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, b, Number)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, c, Number)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "SMS pumping through many accounts at one number");
        Sms(host).Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_number_verifies_one_account_only()
    {
        await using var host = await StartAsync();
        using var client = host.Client();
        var (_, owner) = await SignedInAsync(host, client, "owner");
        var (_, other) = await SignedInAsync(host, client, "other");

        await Send(client, owner, Number);
        (await Verify(client, owner, CodeIn(Sms(host).LastTo(Number)!))).StatusCode.Should().Be(HttpStatusCode.OK);

        await Send(client, other, Number);
        var second = await Verify(client, other, CodeIn(Sms(host).LastTo(Number)!));

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(second)).GetProperty("outcome").GetString().Should().Be("numberInUse");
        (await host.FindByEmailAsync("other@example.com"))!.PhoneNumberConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Numbers_that_are_not_phone_numbers_or_outside_the_allowed_countries_get_no_message()
    {
        await using var host = await StartAsync(o => o.AllowedCountryCallingCodes = ["32", "31"]);
        using var client = host.Client();
        var (_, cookie) = await SignedInAsync(host, client, "picky");

        var invalid = await Send(client, cookie, "call me maybe");
        var national = await Send(client, cookie, "0470 12 34 56");
        var foreign = await Send(client, cookie, "+44 7700 900123");

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        national.StatusCode.Should().Be(HttpStatusCode.BadRequest, "no default country is configured, so a national number is a guess");
        foreign.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(foreign)).GetProperty("outcome").GetString().Should().Be("countryNotAllowed");
        Sms(host).Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_AddPhoneVerification_the_routes_do_not_exist()
    {
        await using var host = await StartAsync(withPhoneVerification: false);
        using var client = host.Client();
        var (_, cookie) = await SignedInAsync(host, client, "plain");

        (await Send(client, cookie, Number)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/manage/phone", cookie)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("+32 470 12 34 56", null, "+32470123456")]
    [InlineData("0032470123456", null, "+32470123456")]
    [InlineData("0470/12.34.56", "32", "+32470123456")]
    [InlineData("(0470) 12-34-56", "32", "+32470123456")]
    [InlineData("+1 (415) 555-0100", null, "+14155550100")]
    public void Phone_numbers_normalize_to_E164(string input, string? defaultCountry, string expected)
    {
        SparkPhoneNumbers.TryNormalize(input, defaultCountry, out var e164).Should().BeTrue();
        e164.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0470123456", null)]
    [InlineData("470123456", "32")]
    [InlineData("+0470123456", null)]
    [InlineData("+32 470 12 34 56 ext 7", null)]
    [InlineData("+1234567890123456", null)]
    [InlineData("+12345", null)]
    [InlineData("+32+470123456", null)]
    public void Non_numbers_are_refused(string? input, string? defaultCountry)
        => SparkPhoneNumbers.TryNormalize(input, defaultCountry, out _).Should().BeFalse();
}
