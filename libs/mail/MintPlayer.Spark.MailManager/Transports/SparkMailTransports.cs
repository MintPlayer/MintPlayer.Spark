using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.MailManager.Transports;

/// <summary>
/// Delivers one built message. MailManager picks SMTP when <c>Spark:Mail:Smtp:Host</c> is set, the
/// pickup folder when <c>Spark:Mail:PickupFolder</c> is set; <c>AddMailTransport&lt;T&gt;()</c> replaces
/// both. None → startup error (#460, D9).
/// </summary>
/// <remarks>
/// Throw <see cref="NonRetryableException"/> for a failure no retry can fix (a 5xx refusal); anything
/// else is retried with the lane's backoff.
/// </remarks>
public interface ISparkMailTransport
{
    /// <summary>Sends <paramref name="message"/> with envelope sender <paramref name="envelopeFrom"/> (the VERP address when enabled) to <paramref name="recipient"/>.</summary>
    Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken);
}

/// <summary>SMTP through MailKit, one connection per send.</summary>
internal sealed class SmtpMailTransport(IOptions<SparkMailOptions> options, ILogger<SmtpMailTransport> logger) : ISparkMailTransport
{
    public async Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken)
    {
        var smtp = options.Value.Smtp;
        using var client = new SmtpClient { Timeout = (int)smtp.Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(smtp.Host!, smtp.Port, ToMailKit(smtp.Security), cancellationToken);
            if (!string.IsNullOrEmpty(smtp.UserName))
                await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? string.Empty, cancellationToken);
            await client.SendAsync(message, envelopeFrom, [recipient], cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
        {
            // Logged LOUDLY: a relay refusing the sender (553 from Postfix ALLOWED_SENDER_DOMAINS)
            // refuses every mail the same way, and a dead-letter per message is otherwise silent.
            logger.LogError(ex,
                "SMTP relay {Host}:{Port} permanently refused a mail ({Status} {ErrorCode}, mailbox {Mailbox}): {Reply}. "
                + "Not retried. If this is the sender, every mail fails the same way — check Spark:Mail:From and the relay's allowed sender domains.",
                smtp.Host, smtp.Port, (int)ex.StatusCode, ex.ErrorCode, ex.Mailbox?.Address, ex.Message);
            throw new NonRetryableException($"SMTP {(int)ex.StatusCode} {ex.ErrorCode}: {ex.Message}", ex);
        }
        catch (AuthenticationException ex)
        {
            logger.LogError(ex, "SMTP relay {Host}:{Port} refused the credentials. Not retried.", smtp.Host, smtp.Port);
            throw new NonRetryableException($"SMTP authentication failed: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or ServiceNotConnectedException or SocketException or IOException or TimeoutException)
        {
            // 4xx, dropped connections, timeouts: the relay may be back on the next attempt.
            logger.LogWarning(ex, "SMTP relay {Host}:{Port} is unavailable or deferred the mail; it will be retried.", smtp.Host, smtp.Port);
            throw;
        }
    }

    internal static SecureSocketOptions ToMailKit(SparkMailSmtpSecurity security) => security switch
    {
        SparkMailSmtpSecurity.None => SecureSocketOptions.None,
        SparkMailSmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SparkMailSmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}

/// <summary>Writes each mail as <c>{deliveryId}.eml</c> into <c>Spark:Mail:PickupFolder</c> — development and demo apps.</summary>
internal sealed class PickupFolderMailTransport(IOptions<SparkMailOptions> options, IHostEnvironment environment) : ISparkMailTransport
{
    internal string Folder => Path.Combine(environment.ContentRootPath, options.Value.PickupFolder!);

    public async Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        // The envelope is not part of an .eml; kept as headers a reader can see.
        message.Headers.Replace("X-Spark-Envelope-From", envelopeFrom.Address);
        message.Headers.Replace("X-Spark-Envelope-To", recipient.Address);
        var name = (message.MessageId ?? Guid.NewGuid().ToString("N")).Split('@')[0];
        var path = Path.Combine(Folder, $"{name}.eml");
        await using var stream = File.Create(path);
        await message.WriteToAsync(stream, cancellationToken);
    }
}
