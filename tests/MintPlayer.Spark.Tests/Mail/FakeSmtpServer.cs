using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// A minimal SMTP server on loopback for the mail tests: records every command and every message's
/// DATA, advertises what it is told to (STARTTLS by default, like boky/postfix), and answers
/// <c>MAIL FROM</c> / <c>RCPT TO</c> with configurable replies. Never implements TLS: a STARTTLS
/// command is recorded and refused with 454.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task acceptLoop;

    public FakeSmtpServer(bool advertiseStartTls = true)
    {
        AdvertiseStartTls = advertiseStartTls;
        listener.Start();
        acceptLoop = Task.Run(AcceptAsync);
    }

    public bool AdvertiseStartTls { get; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The reply to <c>MAIL FROM</c>, e.g. <c>553 5.7.1 Sender address rejected</c>.</summary>
    public string MailFromReply { get; set; } = "250 2.1.0 Ok";

    /// <summary>The reply to <c>RCPT TO</c>, e.g. <c>550 5.1.1 User unknown</c> or <c>451 4.3.0 Try later</c>.</summary>
    public string RcptToReply { get; set; } = "250 2.1.5 Ok";

    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentQueue<string> Messages { get; } = new();

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.test ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                Commands.Enqueue(line);
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake.test");
                        if (AdvertiseStartTls)
                            await writer.WriteLineAsync("250-STARTTLS");
                        await writer.WriteLineAsync("250-8BITMIME");
                        await writer.WriteLineAsync("250 SIZE 10240000");
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 fake.test");
                        break;
                    case "STARTTLS":
                        await writer.WriteLineAsync("454 4.7.0 TLS not available");
                        break;
                    case "MAIL":
                        await writer.WriteLineAsync(MailFromReply);
                        break;
                    case "RCPT":
                        await writer.WriteLineAsync(RcptToReply);
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                            data.Append(dataLine).Append("\r\n");
                        Messages.Enqueue(data.ToString());
                        await writer.WriteLineAsync("250 2.0.0 Ok: queued");
                        break;
                    case "RSET":
                    case "NOOP":
                        await writer.WriteLineAsync("250 2.0.0 Ok");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("502 5.5.2 Command not recognized");
                        break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await acceptLoop; } catch { }
        stop.Dispose();
    }
}
