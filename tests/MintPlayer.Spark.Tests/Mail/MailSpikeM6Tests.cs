using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #460 spike S-M6: MailKit against a relay that advertises STARTTLS (boky/postfix does): what
/// <c>SecureSocketOptions.None</c> and <c>StartTlsWhenAvailable</c> send, the VERP envelope sender, whether a
/// <c>Sender:</c> header appears, and how 5xx / 4xx replies surface (for failure classification).
/// </summary>
public class MailSpikeM6Tests(ITestOutputHelper output)
{
    private static MimeMessage Message()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("App", "noreply@app.example"));
        message.To.Add(new MailboxAddress("", "someone@recipient.example"));
        message.Subject = "S-M6";
        message.MessageId = "d-1234@app.example";
        message.Body = new TextPart("plain") { Text = "hello" };
        return message;
    }

    [Fact]
    public async Task S_M6_None_never_sends_STARTTLS_and_the_VERP_envelope_is_the_MAIL_FROM_only()
    {
        await using var server = new FakeSmtpServer(advertiseStartTls: true);
        using var client = new SmtpClient();

        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);
        var capabilities = client.Capabilities;
        var sender = new MailboxAddress("", "bounces+d-1234@verp.app.example");
        await client.SendAsync(Message(), sender, [MailboxAddress.Parse("someone@recipient.example")]);
        await client.DisconnectAsync(true);

        foreach (var command in server.Commands)
            output.WriteLine($"C: {command}");
        var data = server.Messages.Single();
        var headers = data[..data.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        output.WriteLine(headers);

        capabilities.HasFlag(SmtpCapabilities.StartTLS).Should().BeTrue("the relay advertised it");
        server.Commands.Should().NotContain(c => c.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase), "None means no TLS, advertised or not");
        server.Commands.Should().Contain(c => c.StartsWith("MAIL FROM:<bounces+d-1234@verp.app.example>", StringComparison.OrdinalIgnoreCase));
        headers.Should().NotContain("\r\nSender:", "the envelope sender is not written as a header");
        headers.StartsWith("Sender:", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        headers.Should().Contain("From: App <noreply@app.example>");
        headers.Should().Contain("Message-Id: <d-1234@app.example>");
    }

    [Fact]
    public async Task S_M6_StartTlsWhenAvailable_tries_STARTTLS_and_fails_when_it_is_refused()
    {
        await using var server = new FakeSmtpServer(advertiseStartTls: true);
        using var client = new SmtpClient();

        Exception? failure = null;
        try { await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.StartTlsWhenAvailable); }
        catch (Exception ex) { failure = ex; }

        output.WriteLine($"failure: {failure?.GetType().FullName}: {failure?.Message}");
        server.Commands.Should().Contain("STARTTLS");
        failure.Should().NotBeNull("a relay advertising STARTTLS it cannot complete fails the connection under Auto");
    }

    [Theory]
    [InlineData("mail", "553 5.7.1 <noreply@app.example>: Sender address rejected: not owned by user")]
    [InlineData("rcpt", "550 5.1.1 <someone@recipient.example>: Recipient address rejected: User unknown")]
    [InlineData("rcpt", "451 4.3.0 Temporary lookup failure")]
    public async Task S_M6_refusals_surface_as_SmtpCommandException_with_the_status(string stage, string reply)
    {
        await using var server = new FakeSmtpServer(advertiseStartTls: false);
        if (stage == "mail") server.MailFromReply = reply; else server.RcptToReply = reply;
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);

        Exception? failure = null;
        try { await client.SendAsync(Message()); }
        catch (Exception ex) { failure = ex; }

        var command = failure as SmtpCommandException;
        output.WriteLine($"{reply} -> {failure?.GetType().Name} ErrorCode={command?.ErrorCode} StatusCode={(int?)command?.StatusCode} Mailbox={command?.Mailbox}");
        command.Should().NotBeNull();
        ((int)command!.StatusCode).Should().Be(int.Parse(reply[..3]));
        command.ErrorCode.Should().Be(stage == "mail" ? SmtpErrorCode.SenderNotAccepted : SmtpErrorCode.RecipientNotAccepted);
    }
}
