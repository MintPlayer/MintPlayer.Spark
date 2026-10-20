using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Printing.Agent;

/// <summary><c>PrintAgent</c> in the agent's appsettings.json (or environment variables <c>PrintAgent__…</c>).</summary>
public sealed class PrintAgentSettings
{
    public const string SectionName = "PrintAgent";

    /// <summary>The app's base URL, e.g. <c>https://warehouse.example.com</c>.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>The agent principal's personal access token (<c>spat_…</c>). A secret: environment variable or user config, never a repository.</summary>
    public string Token { get; set; } = "";

    /// <summary>The journal file. Default: next to the agent's data, <c>journal.log</c>.</summary>
    public string JournalPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "journal.log");

    /// <summary>How often the agent asks for jobs. Default 1 s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a printer connection may take. Default 5 s.</summary>
    public TimeSpan PrinterTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A network scale (<c>host:port</c>) and the station it stands at, or null.</summary>
    public string? ScaleAddress { get; set; }

    public string? ScaleStation { get; set; }
}

/// <summary>A job as the app hands it out (<c>POST /spark/printing/agent/pull</c>).</summary>
public sealed record AgentJob(string Id, string Printer, string? Address, string ContentType, int? Dpi, string ContentBase64, string Title);

public sealed record AgentPrinterInfo(string Name, string Address, string Format, string? Station);

public sealed record AgentHeartbeatAnswer(string? LatestVersion, bool UpdateAvailable, IReadOnlyList<AgentPrinterInfo> Printers, int HeartbeatSeconds);

/// <summary>The app, as the agent sees it. <see cref="HttpAgentServer"/> in production; tests inject faults through their own.</summary>
public interface IAgentServer
{
    Task<IReadOnlyList<AgentJob>> PullAsync(int max, CancellationToken cancellationToken);

    /// <summary>True when the app recorded the acknowledgement (or had it already).</summary>
    Task<bool> AcknowledgeAsync(string jobId, bool printed, string? reason, CancellationToken cancellationToken);

    Task<AgentHeartbeatAnswer?> HeartbeatAsync(string version, IReadOnlyList<string> unreachable, IReadOnlyList<string> uncertain, CancellationToken cancellationToken);

    Task ScaleAsync(string station, decimal grams, bool stable, CancellationToken cancellationToken);
}

/// <summary>How sending a job to a printer ended.</summary>
public enum SendOutcome
{
    /// <summary>Every byte was written and the connection closed cleanly.</summary>
    Sent,
    /// <summary>Nothing left the agent (no connection): safe to try again.</summary>
    NotSent,
    /// <summary>The connection broke after bytes went out: a label may or may not have printed.</summary>
    Partial,
}

/// <summary>A printer's raw port (TCP 9100, PRD §4.11).</summary>
public interface IRawPrinter
{
    Task<SendOutcome> SendAsync(string address, byte[] content, CancellationToken cancellationToken);

    Task<bool> IsReachableAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// One pass of the agent (PRD §4.11, W16): acknowledge what printed earlier, pull, and for each job print it at most once
/// (<see cref="PrintJournal"/>). Separated from the host so tests drive it with injected faults.
/// </summary>
public sealed class AgentLoop(IAgentServer server, IRawPrinter printer, PrintJournal journal, ILogger logger)
{
    /// <summary>Runs one pass; returns how many jobs went to a printer.</summary>
    public async Task<int> RunOnceAsync(int max, CancellationToken cancellationToken)
    {
        // 1. Printed earlier, acknowledgement lost: say it again (the app ignores a repeat).
        foreach (var jobId in journal.PendingAcknowledgements())
            if (await TryAsync(() => server.AcknowledgeAsync(jobId, true, null, cancellationToken)))
                journal.Acknowledged(jobId);

        // 2. Uncertain jobs: never resent; failed at the app so a person decides (reprint = a new job).
        foreach (var jobId in journal.Uncertain())
            if (await TryAsync(() => server.AcknowledgeAsync(jobId, false,
                    "The agent stopped while sending this job: check the printer, then reprint it if no label came out.", cancellationToken)))
                journal.Resolved(jobId);

        var jobs = await server.PullAsync(max, cancellationToken);
        var sent = 0;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journal.IsPrinted(job.Id))
            {
                // A redelivery (our acknowledgement was lost): acknowledge, never print twice.
                if (await TryAsync(() => server.AcknowledgeAsync(job.Id, true, null, cancellationToken)))
                    journal.Acknowledged(job.Id);
                continue;
            }
            if (journal.IsUncertain(job.Id))
                continue;   // reported in step 2 next pass
            if (job.Address is null)
            {
                await TryAsync(() => server.AcknowledgeAsync(job.Id, false, "The printer has no address.", cancellationToken));
                continue;
            }

            journal.Started(job.Id);
            var outcome = await printer.SendAsync(job.Address, Convert.FromBase64String(job.ContentBase64), cancellationToken);
            switch (outcome)
            {
                case SendOutcome.Sent:
                    journal.Printed(job.Id);
                    sent++;
                    if (await TryAsync(() => server.AcknowledgeAsync(job.Id, true, null, cancellationToken)))
                        journal.Acknowledged(job.Id);
                    break;
                case SendOutcome.NotSent:
                    // Printer offline: nothing left the agent. The job stays delivered; the app redelivers it later, and the
                    // station is offered the PDF fallback meanwhile.
                    journal.Unstarted(job.Id);
                    logger.LogWarning("Printer {Address} is unreachable; job {JobId} waits.", job.Address, job.Id);
                    break;
                default:
                    logger.LogError("Job {JobId} broke off while sending to {Address}; it is reported, never resent.", job.Id, job.Address);
                    break;
            }
        }
        return sent;
    }

    private async Task<bool> TryAsync(Func<Task<bool>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning(ex, "The app could not be reached; trying again next pass.");
            return false;
        }
    }
}

/// <summary>Raw TCP printing (port 9100): connect, write, close. Reports whether anything left the agent.</summary>
public sealed class TcpRawPrinter(IOptions<PrintAgentSettings> settings) : IRawPrinter
{
    public async Task<SendOutcome> SendAsync(string address, byte[] content, CancellationToken cancellationToken)
    {
        var (host, port) = Split(address);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Value.PrinterTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return SendOutcome.NotSent;
        }
        try
        {
            await using var stream = client.GetStream();
            await stream.WriteAsync(content, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            client.Client.Shutdown(SocketShutdown.Send);
            return SendOutcome.Sent;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return SendOutcome.Partial;
        }
    }

    public async Task<bool> IsReachableAsync(string address, CancellationToken cancellationToken)
    {
        var (host, port) = Split(address);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Value.PrinterTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static (string Host, int Port) Split(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon > 0 && int.TryParse(address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? (address[..colon], port)
            : (address, 9100);
    }
}

/// <summary>The app over HTTP: the agent's endpoints, bearer token of the agent principal.</summary>
public sealed class HttpAgentServer : IAgentServer
{
    private readonly HttpClient http;

    public HttpAgentServer(HttpClient http, IOptions<PrintAgentSettings> settings)
    {
        this.http = http;
        http.BaseAddress = new Uri(settings.Value.ServerUrl.TrimEnd('/') + "/spark/printing/agent/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.Token);
    }

    public async Task<IReadOnlyList<AgentJob>> PullAsync(int max, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("pull", new { max }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<AgentJob>>(cancellationToken) ?? [];
    }

    public async Task<bool> AcknowledgeAsync(string jobId, bool printed, string? reason, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("ack", new { jobId, printed, reason }, cancellationToken);
        // 404: not this agent's job (any more): nothing to repeat.
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
    }

    public async Task<AgentHeartbeatAnswer?> HeartbeatAsync(string version, IReadOnlyList<string> unreachable, IReadOnlyList<string> uncertain, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("heartbeat",
            new { version, machineName = Environment.MachineName, unreachablePrinters = unreachable, uncertainJobs = uncertain }, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<AgentHeartbeatAnswer>(cancellationToken) : null;
    }

    public async Task ScaleAsync(string station, decimal grams, bool stable, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("scale", new { station, grams, stable }, cancellationToken);
    }
}
