using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Bounces;
using MintPlayer.Spark.MailManager.Deliveries;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// The query values of the two mail endpoints, through the real route table: <c>?recipient</c> on
/// <c>POST /spark/mail/bounces</c> and <c>?t</c> on <c>POST /spark/mail/unsubscribe</c>. Measured while
/// the endpoints read <c>Request.Query</c> by hand, and kept when they became <c>[QueryParam]</c>
/// properties (endpoints generator completion M3b, PRD D3a).
/// </summary>
public class MailEndpointQueryTests : SparkTestDriver
{
    private static readonly string Secret = new('s', 40);

    private SparkEndpointFactory _factory = null!;
    private HttpClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [], configureSpark: spark =>
        {
            spark.AddMessaging();
            spark.AddMailManager(o =>
            {
                o.From.Address = "noreply@app.example";
                o.PickupFolder = Path.Combine(Path.GetTempPath(), "spark-mail-endpoint-query-tests");
                o.PublicBaseUrl = "https://app.example";
                o.Bounces.Endpoint.Enabled = true;
                o.Bounces.Endpoint.Secret = Secret;
            });
        });
        _client = _factory.CreateClient();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<string> PostAsync(string url, string body, bool authorized)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "message/rfc822"),
        };
        if (authorized)
            request.Headers.Add("Authorization", $"Bearer {Secret}");
        using var response = await _client.SendAsync(request);
        return $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
    }

    [Fact]
    public async Task The_unsubscribe_token_is_read_from_t()
    {
        var link = _factory.GetService<SparkMailUnsubscribeTokens>().Link("ann@app.example", "news")!;
        var query = link[link.IndexOf('?')..];

        var lines = new List<string>
        {
            $"none: {await PostAsync("/spark/mail/unsubscribe", "", authorized: false)}",
            $"empty: {await PostAsync("/spark/mail/unsubscribe?t=", "", authorized: false)}",
            $"forged: {await PostAsync("/spark/mail/unsubscribe?t=forged", "", authorized: false)}",
            $"other name: {await PostAsync("/spark/mail/unsubscribe?token=" + query[3..], "", authorized: false)}",
            $"valid: {await PostAsync("/spark/mail/unsubscribe" + query, "", authorized: false)}",
        };

        string.Join("\n", lines).Should().Be("""
            none: 400 
            empty: 400 
            forged: 400 
            other name: 400 
            valid: 200 You are unsubscribed.
            """.ReplaceLineEndings("\n"));
        (await _factory.GetService<ISparkMailSuppressions>().IsSuppressedAsync("ann@app.example", "news")).Should().BeTrue();
    }

    [Fact]
    public async Task The_envelope_recipient_is_read_from_recipient()
    {
        var deliveryId = SparkMailer.NewDeliveryId();
        await _factory.GetService<SparkMailDeliveryStore>().RecordAsync(deliveryId, d => { d.Email = "ann@app.example"; d.Status = SparkMailDeliveryStatus.Sent; }, default);
        var suppressions = _factory.GetService<ISparkMailSuppressions>();
        // Addressed to a plain mailbox, so only the envelope recipient can name the delivery.
        var dsn = Dsn("postmaster@bounce.app.example");

        var lines = new List<string>
        {
            $"unauthorized: {await PostAsync($"/spark/mail/bounces?recipient=bounces%2B{deliveryId}%40bounce.app.example", dsn, authorized: false)}",
            $"no recipient: {await PostAsync("/spark/mail/bounces", dsn, authorized: true)}",
        };
        lines.Add($"suppressed after no recipient: {await suppressions.IsSuppressedAsync("ann@app.example")}");
        lines.Add($"recipient: {await PostAsync($"/spark/mail/bounces?recipient=bounces%2B{deliveryId}%40bounce.app.example", dsn, authorized: true)}");
        lines.Add($"suppressed after recipient: {await suppressions.IsSuppressedAsync("ann@app.example")}");
        lines.Add($"unparseable: {await PostAsync("/spark/mail/bounces?recipient=x", "not a report", authorized: true)}");

        string.Join("\n", lines).Should().Be("""
            unauthorized: 401 
            no recipient: 204 
            suppressed after no recipient: False
            recipient: 204 
            suppressed after recipient: True
            unparseable: 400 
            """.ReplaceLineEndings("\n"));
    }

    private static string Dsn(string to) =>
        $"From: MAILER-DAEMON@relay.example\r\nTo: {to}\r\nSubject: Undelivered Mail\r\nMIME-Version: 1.0\r\n" +
        "Content-Type: multipart/report; report-type=delivery-status; boundary=\"b\"\r\n\r\n" +
        "--b\r\nContent-Type: text/plain\r\n\r\nThe mail could not be delivered.\r\n" +
        "--b\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; relay.example\r\n\r\n" +
        "Final-Recipient: rfc822; ann@app.example\r\nAction: failed\r\nStatus: 5.1.1\r\nDiagnostic-Code: smtp; 550 5.1.1 User unknown\r\n\r\n" +
        "--b--\r\n";
}
