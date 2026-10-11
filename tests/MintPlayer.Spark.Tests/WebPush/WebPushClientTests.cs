using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.WebPush;

namespace MintPlayer.Spark.Tests.WebPush;

/// <summary>
/// RFC 8292 VAPID and the RFC 8030 request (<c>docs/seo_analyzer_PRD.md</c> D38): what goes on the wire,
/// and how every push-service answer maps to delete / retry / give up.
/// </summary>
public class WebPushClientTests
{
    private static readonly VapidDetails Vapid = VapidDetails.Generate("mailto:ops@example.com");

    [Fact]
    public async Task The_request_carries_the_encrypted_body_and_the_RFC_8030_headers()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        using var vapid = new VapidAuthorization(Vapid);
        var client = new WebPushClient(new HttpClient(handler), vapid);
        var (subscription, userAgent, auth) = NewSubscription("https://fcm.googleapis.com/fcm/send/abc:def");

        var result = await client.SendAsync(subscription, new WebPushMessage
        {
            Payload = Encoding.UTF8.GetBytes("hello"),
            TimeToLive = TimeSpan.FromHours(2),
            Urgency = WebPushUrgency.VeryLow,
            Topic = "score-drop_1",
        });

        result.Outcome.Should().Be(WebPushOutcome.Delivered);
        var request = handler.Requests.Single();
        request.Method.Should().Be("POST");
        request.Uri.Should().Be("https://fcm.googleapis.com/fcm/send/abc:def");
        request.Headers["TTL"].Should().Be("7200");
        request.Headers["Urgency"].Should().Be("very-low");
        request.Headers["Topic"].Should().Be("score-drop_1");
        request.Headers["Content-Encoding"].Should().Be("aes128gcm");
        request.Headers["Content-Type"].Should().Be("application/octet-stream");
        WebPushEncryptionTests.Receiver.Derive(request.Body, userAgent, auth).Plaintext.SequenceEqual("hello"u8.ToArray()).Should().BeTrue();
        userAgent.Dispose();
    }

    [Fact]
    public async Task The_VAPID_header_is_an_ES256_JWT_for_the_push_service_origin_that_verifies_with_the_public_key()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var time = new ManualClock(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        using var vapid = new VapidAuthorization(Vapid, time);
        var client = new WebPushClient(new HttpClient(handler), vapid);
        var (subscription, userAgent, _) = NewSubscription("https://updates.push.services.mozilla.com:443/wpush/v2/gAAAA");
        userAgent.Dispose();

        await client.SendAsync(subscription, WebPushMessage.FromText("x"));

        var header = handler.Requests.Single().Headers["Authorization"];
        header.Should().StartWith("vapid t=");
        var parts = header["vapid ".Length..].Split(", ");
        var token = parts[0]["t=".Length..];
        parts[1].Should().Be("k=" + Vapid.PublicKey);

        var segments = token.Split('.');
        segments.Length.Should().Be(3);
        using var head = JsonDocument.Parse(Base64Url.DecodeFromChars(segments[0]));
        head.RootElement.GetProperty("alg").GetString().Should().Be("ES256");
        head.RootElement.GetProperty("typ").GetString().Should().Be("JWT");
        using var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(segments[1]));
        claims.RootElement.GetProperty("aud").GetString().Should().Be("https://updates.push.services.mozilla.com"); // origin, default port dropped
        claims.RootElement.GetProperty("sub").GetString().Should().Be("mailto:ops@example.com");
        claims.RootElement.GetProperty("exp").GetInt64().Should().Be(time.GetUtcNow().AddHours(12).ToUnixTimeSeconds());

        var signature = Base64Url.DecodeFromChars(segments[2]);
        signature.Length.Should().Be(64); // IEEE P1363 r‖s, not DER
        var q = Base64Url.DecodeFromChars(Vapid.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q[1..33], Y = q[33..65] } });
        verifier.VerifyData(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]), signature, HashAlgorithmName.SHA256).Should().BeTrue();
    }

    [Fact]
    public void A_token_is_reused_per_origin_until_an_hour_before_it_expires()
    {
        var time = new ManualClock(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        using var vapid = new VapidAuthorization(Vapid, time);
        var apple = new Uri("https://web.push.apple.com/QGuQyavXutnMH");
        var appleOther = new Uri("https://web.push.apple.com/another");
        var google = new Uri("https://fcm.googleapis.com/fcm/send/x");

        var first = vapid.HeaderFor(apple);
        vapid.HeaderFor(appleOther).Should().Be(first);       // same origin, same token
        vapid.HeaderFor(google).Should().NotBe(first);        // aud differs
        time.Advance(TimeSpan.FromHours(10.9));
        vapid.HeaderFor(apple).Should().Be(first);            // 1.1 h left: kept (Apple: at most one refresh an hour)
        time.Advance(TimeSpan.FromHours(0.2));
        vapid.HeaderFor(apple).Should().NotBe(first);         // 0.9 h left: renewed
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, WebPushOutcome.Delivered)]
    [InlineData(HttpStatusCode.OK, WebPushOutcome.Delivered)]
    [InlineData(HttpStatusCode.NotFound, WebPushOutcome.SubscriptionGone)]
    [InlineData(HttpStatusCode.Gone, WebPushOutcome.SubscriptionGone)]
    [InlineData(HttpStatusCode.TooManyRequests, WebPushOutcome.RetryLater)]
    [InlineData(HttpStatusCode.InternalServerError, WebPushOutcome.RetryLater)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WebPushOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadRequest, WebPushOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, WebPushOutcome.Rejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, WebPushOutcome.Rejected)]
    public async Task Every_answer_maps_to_what_the_sender_does_next(HttpStatusCode status, WebPushOutcome expected)
    {
        using var vapid = new VapidAuthorization(Vapid);
        var handler = new RecordingHandler(status, retryAfter: status == HttpStatusCode.TooManyRequests ? TimeSpan.FromSeconds(120) : null, body: "{\"reason\":\"BadJwtToken\"}");
        var (subscription, userAgent, _) = NewSubscription("https://web.push.apple.com/abc");
        userAgent.Dispose();

        var result = await new WebPushClient(new HttpClient(handler), vapid).SendAsync(subscription, WebPushMessage.FromText("x"));

        result.Outcome.Should().Be(expected);
        result.StatusCode.Should().Be(status);
        if (status == HttpStatusCode.TooManyRequests)
            result.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
        if (expected != WebPushOutcome.Delivered)
            result.Reason.Should().Be("{\"reason\":\"BadJwtToken\"}");
    }

    [Theory]
    [InlineData("http://push.example.net/x")]
    [InlineData("push.example.net/x")]
    public async Task Only_https_endpoints_are_sent_to(string endpoint)
    {
        using var vapid = new VapidAuthorization(Vapid);
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var (subscription, userAgent, _) = NewSubscription(endpoint);
        userAgent.Dispose();

        var act = () => new WebPushClient(new HttpClient(handler), vapid).SendAsync(subscription, WebPushMessage.FromText("x"));
        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("0123456789012345678901234567890123")] // 34
    public async Task A_topic_outside_RFC_8030_is_refused(string topic)
    {
        using var vapid = new VapidAuthorization(Vapid);
        var (subscription, userAgent, _) = NewSubscription("https://push.example.net/x");
        userAgent.Dispose();
        var act = () => new WebPushClient(new HttpClient(new RecordingHandler(HttpStatusCode.Created)), vapid)
            .SendAsync(subscription, new WebPushMessage { Payload = "x"u8.ToArray(), Topic = topic });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void VAPID_keys_that_are_not_a_pair_or_a_bad_subject_are_refused_at_construction()
    {
        var other = VapidDetails.Generate("mailto:a@example.com");
        var mismatched = Vapid with { PrivateKey = other.PrivateKey };
        var act = () => new VapidAuthorization(mismatched);
        act.Should().Throw<ArgumentException>();

        var badSubject = () => new VapidAuthorization(Vapid with { Subject = "ops@example.com" });
        badSubject.Should().Throw<ArgumentException>();

        var https = () => new VapidAuthorization(Vapid with { Subject = "https://seotester.mintplayer.com/bot" });
        https.Should().NotThrow();
    }

    private static (WebPushSubscription Subscription, ECDiffieHellman UserAgent, byte[] Auth) NewSubscription(string endpoint)
    {
        var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        return (new WebPushSubscription(endpoint, Base64Url.EncodeToString(WebPushEncryptionTests.Uncompressed(userAgent)), Base64Url.EncodeToString(auth)), userAgent, auth);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    internal sealed class RecordingHandler(HttpStatusCode status, TimeSpan? retryAfter = null, string? body = null) : HttpMessageHandler
    {
        public sealed record Recorded(string Method, string Uri, Dictionary<string, string> Headers, byte[] Body);

        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var content = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Content is not null)
                foreach (var h in request.Content.Headers)
                    headers[h.Key] = string.Join(",", h.Value);
            Requests.Add(new Recorded(request.Method.Method, request.RequestUri!.ToString(), headers, content));

            var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? "") };
            if (retryAfter is { } wait)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            return response;
        }
    }
}
