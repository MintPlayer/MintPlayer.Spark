using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Printing.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Printing.Services;

/// <summary>Picks the printer for a job (PRD §4.11: "a job is routed to a printer"). Replace it to route by other rules.</summary>
public interface IPrintRouter
{
    /// <summary>The printer's name, or null when none fits and there is no default.</summary>
    string? Route(string kind, string? site, string? station);
}

/// <summary>
/// The built-in rule: among the printers that take <c>kind</c> (or every kind), the one of the station beats the one of the
/// site, which beats a site-less one; a printer of another station or site never matches; a printer that names the kind
/// beats one that takes everything. Otherwise the default printer.
/// </summary>
internal sealed partial class ConfiguredPrintRouter : IPrintRouter
{
    [Inject] private readonly IOptions<SparkPrintingOptions> options;

    public string? Route(string kind, string? site, string? station)
    {
        string? best = null;
        var bestScore = int.MinValue;
        foreach (var (name, printer) in options.Value.Printers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var takesKind = printer.LabelKinds.Count == 0 || printer.LabelKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
            if (!takesKind)
                continue;
            if (printer.Station is not null && !string.Equals(printer.Station, station, StringComparison.OrdinalIgnoreCase))
                continue;
            if (printer.Site is not null && !string.Equals(printer.Site, site, StringComparison.OrdinalIgnoreCase))
                continue;
            var score = (printer.Station is not null ? 4 : 0) + (printer.Site is not null ? 2 : 0) + (printer.LabelKinds.Count > 0 ? 1 : 0);
            if (score > bestScore)
            {
                best = name;
                bestScore = score;
            }
        }
        return best ?? options.Value.Printers.FirstOrDefault(p => p.Value.Default).Key;
    }
}

/// <summary>Agents (Q-W14): who is calling, their heartbeat and version, the update check; scale readings they pass on.</summary>
public interface IPrintAgents
{
    /// <summary>The configured agent whose principal is <paramref name="userId"/>, or null.</summary>
    string? AgentOf(string? userId);

    Task<PrintHeartbeatResponse> HeartbeatAsync(string agent, PrintHeartbeatRequest heartbeat, CancellationToken cancellationToken = default);

    /// <summary>Records a scale reading at a station the agent serves (a station of one of its printers).</summary>
    Task<bool> RecordScaleAsync(string agent, string station, decimal grams, bool stable, CancellationToken cancellationToken = default);

    /// <summary>The station's last stable reading, if it is younger than <paramref name="maxAge"/>.</summary>
    Task<ScaleReading?> LatestScaleAsync(string station, TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>Whether the agent's last heartbeat is older than <c>AgentOfflineAfter</c> (or there is none).</summary>
    Task<bool> IsOfflineAsync(string agent, CancellationToken cancellationToken = default);
}

/// <param name="UnreachablePrinters">Printers the agent could not open a connection to.</param>
/// <param name="UncertainJobs">Jobs its journal shows started but not finished (it stopped while sending).</param>
public sealed record PrintHeartbeatRequest(string? Version, string? MachineName, IReadOnlyList<string>? UnreachablePrinters, IReadOnlyList<string>? UncertainJobs);

/// <param name="UpdateAvailable">True when <see cref="LatestVersion"/> is newer than the agent's.</param>
/// <param name="Printers">The printers this agent serves: name, address and format, so its configuration lives in the app.</param>
public sealed record PrintHeartbeatResponse(string? LatestVersion, bool UpdateAvailable, IReadOnlyList<AgentPrinter> Printers, int HeartbeatSeconds);

public sealed record AgentPrinter(string Name, string Address, string Format, string? Station);

internal sealed partial class PrintAgents : IPrintAgents
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkPrintingOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    public string? AgentOf(string? userId)
        => userId is { Length: > 0 }
            ? options.Value.Agents.FirstOrDefault(a => string.Equals(a.Value.UserId, userId, StringComparison.Ordinal)).Key
            : null;

    public async Task<PrintHeartbeatResponse> HeartbeatAsync(string agent, PrintHeartbeatRequest heartbeat, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        using var session = documentStore.OpenAsyncSession();
        var id = PrintAgentStatus.IdFor(agent);
        var status = await session.LoadAsync<PrintAgentStatus>(id, cancellationToken);
        if (status is null)
        {
            status = new PrintAgentStatus { Id = id, Name = agent };
            await session.StoreAsync(status, id, cancellationToken);
        }
        status.Version = Clip(heartbeat.Version, 64);
        status.MachineName = Clip(heartbeat.MachineName, 128);
        status.LastHeartbeatAt = timeProvider.GetUtcNow();
        status.UnreachablePrinters = (heartbeat.UnreachablePrinters ?? []).Take(64).Select(p => Clip(p, 128)!).ToList();
        status.UncertainJobs = (heartbeat.UncertainJobs ?? []).Take(256).Select(j => Clip(j, 512)!).ToList();
        await session.SaveChangesAsync(cancellationToken);

        var printers = o.Printers
            .Where(p => string.Equals(p.Value.Adapter, PrinterAdapters.Agent, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Value.Agent, agent, StringComparison.OrdinalIgnoreCase))
            .Select(p => new AgentPrinter(p.Key, p.Value.Address!, p.Value.Format.ToString(), p.Value.Station))
            .ToList();
        return new PrintHeartbeatResponse(o.LatestAgentVersion, IsOlder(heartbeat.Version, o.LatestAgentVersion), printers,
            Math.Max(1, (int)(o.AgentOfflineAfter.TotalSeconds / 3)));
    }

    public async Task<bool> RecordScaleAsync(string agent, string station, decimal grams, bool stable, CancellationToken cancellationToken = default)
    {
        var serves = options.Value.Printers.Values.Any(p => string.Equals(p.Agent, agent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Station, station, StringComparison.OrdinalIgnoreCase));
        if (!serves || grams < 0 || grams > 10_000_000)
            return false;
        using var session = documentStore.OpenAsyncSession();
        var id = ScaleReading.IdFor(station);
        var reading = await session.LoadAsync<ScaleReading>(id, cancellationToken);
        if (reading is null)
        {
            reading = new ScaleReading { Id = id };
            await session.StoreAsync(reading, id, cancellationToken);
        }
        reading.Station = station;
        reading.Agent = agent;
        reading.Grams = grams;
        reading.Stable = stable;
        reading.ReadAt = timeProvider.GetUtcNow();
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ScaleReading?> LatestScaleAsync(string station, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var reading = await session.LoadAsync<ScaleReading>(ScaleReading.IdFor(station), cancellationToken);
        return reading is { Stable: true } && reading.ReadAt >= timeProvider.GetUtcNow() - maxAge ? reading : null;
    }

    public async Task<bool> IsOfflineAsync(string agent, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var status = await session.LoadAsync<PrintAgentStatus>(PrintAgentStatus.IdFor(agent), cancellationToken);
        return status?.LastHeartbeatAt is not { } beat || beat < timeProvider.GetUtcNow() - options.Value.AgentOfflineAfter;
    }

    /// <summary>Whether <paramref name="version"/> is older than <paramref name="latest"/> (both System.Version strings; unparsable: not older).</summary>
    internal static bool IsOlder(string? version, string? latest)
        => Version.TryParse(StripSuffix(version), out var v) && Version.TryParse(StripSuffix(latest), out var l) && v < l;

    private static string? StripSuffix(string? version) => version?.Split('-', '+')[0];

    private static string? Clip(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}
