using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Printing;

/// <summary>
/// <c>Spark:Printing</c> (PRD §4.13: <c>Spark:Printing:Printers:{name}</c>, adapter and format). Printers and agents are
/// configuration: the site's hardware is the host's, and startup refuses a printer whose adapter package is not
/// registered (<see cref="SparkPrintingOptionsValidator"/>).
/// </summary>
/// <example>
/// <code>
/// "Spark": { "Printing": {
///   "Agents":   { "dock-1": { "UserId": "users/print-agent-dock-1" } },
///   "Printers": {
///     "dock-zebra": { "Adapter": "Agent", "Agent": "dock-1", "Address": "10.0.0.21:9100", "Format": "Zpl203",
///                     "Site": "warehouse-sites/BRU", "Station": "pack-1", "LabelKinds": [ "Shipping", "Sscc" ] },
///     "office":     { "Adapter": "Pdf", "Format": "PdfA4", "Default": true } } } }
/// </code>
/// </example>
public sealed class SparkPrintingOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Printing";

    /// <summary>The printers by name (the name is the job's printer id).</summary>
    public Dictionary<string, PrinterOptions> Printers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The on-site agents by name (Q-W14): each is a dedicated principal holding only agent rights.</summary>
    public Dictionary<string, PrintAgentOptions> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The authentication scheme agent requests use: a Spark personal access token (Q-W14; Authorization registers it as
    /// <c>SparkPersonalAccessToken</c>). Agent endpoints accept nothing else.
    /// </summary>
    public string AgentAuthenticationScheme { get; set; } = "SparkPersonalAccessToken";

    /// <summary>An agent is offline when its last heartbeat is older than this (2 s – 5 min). Default 9 s (heartbeat every 3 s).</summary>
    public TimeSpan AgentOfflineAfter { get; set; } = TimeSpan.FromSeconds(9);

    /// <summary>
    /// A job for an agent printer that has not printed this long after it was queued is offered at its station as a PDF
    /// (WD7, W16: within 10 s of the agent going offline). 2 s – 10 min. Default 10 s.
    /// </summary>
    public TimeSpan FallbackAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A delivered job not acknowledged this long is delivered again (the agent's journal makes that a no-op if it printed). Default 60 s.</summary>
    public TimeSpan RedeliverAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The agent version the app expects; an agent reporting an older one is shown as outdated (Q-W14).</summary>
    public string? LatestAgentVersion { get; set; }

    /// <summary>How long finished jobs are kept (1 day – 1 year). Default 30 days.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>One printer.</summary>
public sealed class PrinterOptions
{
    /// <summary>The adapter: <c>Pdf</c> (built in), <c>Agent</c> (built in), <c>QzTray</c>, <c>ZebraBrowserPrint</c>, or another registered one.</summary>
    public string Adapter { get; set; } = PrinterAdapters.Pdf;

    /// <summary>What the printer takes: <c>Zpl203</c>, <c>Zpl300</c>, <c>PdfA6</c>, <c>Pdf4x6</c> or <c>PdfA4</c>.</summary>
    public PrintFormat Format { get; set; } = PrintFormat.PdfA4;

    /// <summary>For <c>Agent</c>: the agent's name in <see cref="SparkPrintingOptions.Agents"/>.</summary>
    public string? Agent { get; set; }

    /// <summary>For <c>Agent</c>: the printer's raw port, <c>host:9100</c>.</summary>
    public string? Address { get; set; }

    /// <summary>For a bridge (<c>QzTray</c>, <c>ZebraBrowserPrint</c>): the printer's name as the bridge knows it.</summary>
    public string? DeviceName { get; set; }

    /// <summary>The site it stands in (a document id), or null for any.</summary>
    public string? Site { get; set; }

    /// <summary>The station it serves (a pack station, a receiving desk), or null for the whole site.</summary>
    public string? Station { get; set; }

    /// <summary>The label kinds routed here (<see cref="LabelKinds"/>); empty: every kind.</summary>
    public List<string> LabelKinds { get; set; } = [];

    /// <summary>The printer used when no other route matches.</summary>
    public bool Default { get; set; }
}

/// <summary>One on-site agent.</summary>
public sealed class PrintAgentOptions
{
    /// <summary>The agent principal's user id: its personal access token authenticates as this user (Q-W14).</summary>
    public string UserId { get; set; } = "";
}

/// <summary>What a printer takes.</summary>
public enum PrintFormat
{
    Zpl203,
    Zpl300,
    PdfA6,
    Pdf4x6,
    PdfA4,
}

/// <summary>The built-in adapter names and the ones the bridge packages register.</summary>
public static class PrinterAdapters
{
    /// <summary>The browser's print dialog, built in (zero install, always works, shows the dialog).</summary>
    public const string Pdf = "Pdf";

    /// <summary>The on-site agent (MintPlayer.Spark.Printing.Agent), built in on the server side.</summary>
    public const string Agent = "Agent";

    /// <summary>MintPlayer.Spark.Printing.QzTray.</summary>
    public const string QzTray = "QzTray";

    /// <summary>MintPlayer.Spark.Printing.ZebraBrowserPrint.</summary>
    public const string ZebraBrowserPrint = "ZebraBrowserPrint";
}

/// <summary>The label kinds the warehouse prints; a host may route others by name.</summary>
public static class LabelKinds
{
    public const string Location = "Location";
    public const string Item = "Item";
    public const string Tote = "Tote";
    public const string Sscc = "Sscc";
    public const string Shipping = "Shipping";
    public const string PackingSlip = "PackingSlip";
    public const string PickList = "PickList";
}

/// <summary>A registered printer adapter (the bridge packages add theirs; <c>Pdf</c> and <c>Agent</c> are built in).</summary>
/// <param name="DeliveredByStation">True when the station's browser delivers the job (the PDF dialog and the bridges); false for the agent.</param>
public sealed record PrinterAdapterRegistration(string Name, bool DeliveredByStation);

/// <summary>Startup validation of <see cref="SparkPrintingOptions"/> (PRD §4.13: "a printer whose adapter package isn't registered").</summary>
internal sealed class SparkPrintingOptionsValidator(IEnumerable<PrinterAdapterRegistration> adapters) : IValidateOptions<SparkPrintingOptions>
{
    public ValidateOptionsResult Validate(string? name, SparkPrintingOptions options)
    {
        var problems = Problems(options, adapters.Select(a => a.Name)).ToList();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    internal static IEnumerable<string> Problems(SparkPrintingOptions options, IEnumerable<string> adapterNames)
    {
        var known = adapterNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (printer, p) in options.Printers)
        {
            if (!known.Contains(p.Adapter))
                yield return $"Spark:Printing:Printers:{printer}:Adapter '{p.Adapter}' is not registered ({string.Join(", ", known.Order())}); add its package (e.g. AddPrintingQzTray()).";
            if (string.Equals(p.Adapter, PrinterAdapters.Agent, StringComparison.OrdinalIgnoreCase))
            {
                if (p.Agent is null || !options.Agents.ContainsKey(p.Agent))
                    yield return $"Spark:Printing:Printers:{printer}:Agent must name an agent under Spark:Printing:Agents.";
                if (!IsHostAndPort(p.Address))
                    yield return $"Spark:Printing:Printers:{printer}:Address must be host:port (the printer's raw port, usually 9100).";
            }
            if (p.Format is PrintFormat.Zpl203 or PrintFormat.Zpl300 && string.Equals(p.Adapter, PrinterAdapters.Pdf, StringComparison.OrdinalIgnoreCase))
                yield return $"Spark:Printing:Printers:{printer}: the PDF dialog prints PDF; use a Pdf format or another adapter for ZPL.";
        }
        if (options.Printers.Count(p => p.Value.Default) > 1)
            yield return "Spark:Printing:Printers: at most one printer is the Default.";
        foreach (var (agent, a) in options.Agents)
            if (string.IsNullOrWhiteSpace(a.UserId))
                yield return $"Spark:Printing:Agents:{agent}:UserId is empty (the agent principal's user id).";
        if (options.Agents.Values.GroupBy(a => a.UserId, StringComparer.Ordinal).Any(g => g.Key.Length > 0 && g.Count() > 1))
            yield return "Spark:Printing:Agents: two agents share one UserId; give each agent its own principal.";
        if (options.AgentOfflineAfter < TimeSpan.FromSeconds(2) || options.AgentOfflineAfter > TimeSpan.FromMinutes(5))
            yield return $"Spark:Printing:AgentOfflineAfter must be 2 s – 5 min (is {options.AgentOfflineAfter}).";
        if (options.FallbackAfter < TimeSpan.FromSeconds(2) || options.FallbackAfter > TimeSpan.FromMinutes(10))
            yield return $"Spark:Printing:FallbackAfter must be 2 s – 10 min (is {options.FallbackAfter}).";
        if (options.RedeliverAfter < TimeSpan.FromSeconds(5))
            yield return $"Spark:Printing:RedeliverAfter must be at least 5 s (is {options.RedeliverAfter}).";
        if (options.Retention < TimeSpan.FromDays(1) || options.Retention > TimeSpan.FromDays(366))
            yield return $"Spark:Printing:Retention must be 1 day – 1 year (is {options.Retention}).";
    }

    private static bool IsHostAndPort(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;
        var colon = address.LastIndexOf(':');
        return colon > 0 && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and < 65536;
    }
}
