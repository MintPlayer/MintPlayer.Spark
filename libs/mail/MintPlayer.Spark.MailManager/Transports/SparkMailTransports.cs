using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.MailManager.Transports;

/// <summary>What the handler knows about a mail beyond its MIME, for a transport that wants it.</summary>
/// <param name="DeliveryId">The delivery id (also the local part of the <c>Message-ID</c>).</param>
/// <param name="Template">The template name (<c>SparkAuth/ConfirmEmail</c>).</param>
/// <param name="Stream">The unsubscribe / suppression stream.</param>
/// <param name="Lane">The messaging lane it was sent on: <see cref="Messaging.SparkMailQueues.Transactional"/> or <see cref="Messaging.SparkMailQueues.Bulk"/>.</param>
public sealed record SparkMailSendContext(string DeliveryId, string Template, string Stream, string Lane);

/// <summary>
/// Delivers one built message. Register exactly one: <c>UseSmtpTransport()</c>,
/// <c>UseMailpitTransport()</c>, <c>UsePickupFolderTransport()</c> or <c>AddMailTransport&lt;T&gt;()</c>.
/// With none registered, MailManager falls back to the configuration: SMTP when
/// <c>Spark:Mail:Smtp:Host</c> is set, the pickup folder when <c>Spark:Mail:PickupFolder</c> is set;
/// neither → startup error (#460, D9).
/// </summary>
/// <remarks>
/// Throw <see cref="NonRetryableException"/> for a failure no retry can fix (a 5xx refusal); anything
/// else is retried with the lane's backoff.
/// </remarks>
public interface ISparkMailTransport
{
    /// <summary>Sends <paramref name="message"/> with envelope sender <paramref name="envelopeFrom"/> (the VERP address when enabled) to <paramref name="recipient"/>.</summary>
    Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken);

    /// <summary>
    /// What MailManager calls. The default ignores <paramref name="context"/>; a transport that tags or
    /// routes by template or lane overrides it.
    /// </summary>
    Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, SparkMailSendContext context, CancellationToken cancellationToken)
        => SendAsync(message, envelopeFrom, recipient, cancellationToken);

    /// <summary>
    /// Whether mail sent through this transport reaches the people it is addressed to. In Development,
    /// a transport that does and no <c>Spark:Mail:Development:RedirectTo</c> refuses startup (fail
    /// closed). Defaults to <see langword="true"/>, the safe answer for a transport MailManager does not know.
    /// </summary>
    bool DeliversToRealRecipients => true;
}

/// <summary>SMTP through MailKit, one connection per send (<c>Spark:Mail:Smtp</c>).</summary>
internal sealed class SmtpMailTransport(IOptions<SparkMailOptions> options, ILogger<SmtpMailTransport> logger) : ISparkMailTransport
{
    /// <summary>A loopback relay is a local catcher, not the internet.</summary>
    public bool DeliversToRealRecipients => !IsLoopback(options.Value.Smtp.Host);

    public Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken)
    {
        var smtp = options.Value.Smtp;
        return SendAsync(logger, smtp.Host!, smtp.Port, ToMailKit(smtp.Security), smtp.UserName, smtp.Password, smtp.Timeout,
            message, envelopeFrom, recipient, cancellationToken);
    }

    /// <summary><c>localhost</c>, <c>127.0.0.0/8</c> or <c>::1</c> (brackets allowed).</summary>
    internal static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().TrimStart('[').TrimEnd(']');
        return string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>One MailKit send with the failure classification every SMTP-speaking transport shares.</summary>
    internal static async Task SendAsync(ILogger logger, string host, int port, SecureSocketOptions security, string? userName, string? password, TimeSpan timeout,
        MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = (int)timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(host, port, security, cancellationToken);
            if (!string.IsNullOrEmpty(userName))
                await client.AuthenticateAsync(userName, password ?? string.Empty, cancellationToken);
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
                host, port, (int)ex.StatusCode, ex.ErrorCode, ex.Mailbox?.Address, ex.Message);
            throw new NonRetryableException($"SMTP {(int)ex.StatusCode} {ex.ErrorCode}: {ex.Message}", ex);
        }
        catch (AuthenticationException ex)
        {
            logger.LogError(ex, "SMTP relay {Host}:{Port} refused the credentials. Not retried.", host, port);
            throw new NonRetryableException($"SMTP authentication failed: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or ServiceNotConnectedException or SocketException or IOException or TimeoutException)
        {
            // 4xx, dropped connections, timeouts: the relay may be back on the next attempt.
            logger.LogWarning(ex, "SMTP relay {Host}:{Port} is unavailable or deferred the mail; it will be retried.", host, port);
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

/// <summary>
/// SMTP into a local <see href="https://mailpit.axllent.org/">Mailpit</see> (<c>Spark:Mail:Mailpit</c>;
/// default <c>localhost:1025</c>, no TLS, no authentication). SMTP rather than Mailpit's JSON send API,
/// so Mailpit shows the exact MIME a relay would get. Adds Mailpit's <c>X-Tags</c>: the template, the
/// lane (<c>transactional</c>/<c>bulk</c>) and <see cref="SparkMailMailpitOptions.Tags"/>.
/// </summary>
internal sealed class MailpitMailTransport(IOptions<SparkMailOptions> options, ILogger<MailpitMailTransport> logger) : ISparkMailTransport
{
    public bool DeliversToRealRecipients => false;

    public Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken)
        => SendCoreAsync(message, envelopeFrom, recipient, context: null, cancellationToken);

    public Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, SparkMailSendContext context, CancellationToken cancellationToken)
        => SendCoreAsync(message, envelopeFrom, recipient, context, cancellationToken);

    private Task SendCoreAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, SparkMailSendContext? context, CancellationToken cancellationToken)
    {
        var mailpit = options.Value.Mailpit;
        var tags = new List<string>();
        if (context is not null)
        {
            tags.Add(Tag(context.Template));
            tags.Add(Tag(context.Lane.StartsWith("mail-", StringComparison.Ordinal) ? context.Lane["mail-".Length..] : context.Lane));
        }
        tags.AddRange(mailpit.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Tag));
        var distinct = tags.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count > 0)
            message.Headers.Replace("X-Tags", string.Join(", ", distinct));

        return SmtpMailTransport.SendAsync(logger, mailpit.Host, mailpit.Port, SecureSocketOptions.None, null, null, mailpit.Timeout,
            message, envelopeFrom, recipient, cancellationToken);
    }

    /// <summary>A Mailpit tag: letters, digits, <c>.</c>, <c>_</c>, <c>-</c> and spaces; anything else (the <c>/</c> of a template path) becomes <c>-</c>.</summary>
    internal static string Tag(string value)
    {
        var tag = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            tag.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' ? c : '-');
        return tag.ToString();
    }
}

/// <summary>Writes each mail as <c>{deliveryId}.eml</c> into <c>Spark:Mail:PickupFolder</c> — development and demo apps.</summary>
internal sealed class PickupFolderMailTransport(IOptions<SparkMailOptions> options, IHostEnvironment environment) : ISparkMailTransport
{
    internal string Folder => Path.Combine(environment.ContentRootPath, options.Value.PickupFolder!);

    public bool DeliversToRealRecipients => false;

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
