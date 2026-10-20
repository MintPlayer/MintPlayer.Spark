using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Printing.Models;

/// <summary>Where a job is (PRD §4.11, Jobs: queued, delivered, printed, failed).</summary>
public enum PrintJobState
{
    Queued,
    /// <summary>Handed to the agent or the station; not acknowledged yet. Redelivered after <c>RedeliverAfter</c>.</summary>
    Delivered,
    Printed,
    Failed,
}

/// <summary>
/// A print job (PRD §4.11): <c>print-jobs/{key}</c>, where the key is the caller's idempotency key (a shipment's label, a
/// receipt line's labels), so submitting the same job twice stores it once. The content is rendered for its printer when it
/// is queued: ZPL text at the printer's resolution, or PDF bytes; a ZPL job also keeps a PDF of the same label for the
/// fallback (<see cref="FallbackPdf"/>). A reprint is a new job (<see cref="ReprintOf"/>).
/// </summary>
public sealed class PrintJob : IExpiring
{
    public const string IdPrefix = "print-jobs/";

    public static string IdFor(string key) => IdPrefix + Uri.EscapeDataString(key.Trim());

    public string? Id { get; set; }

    /// <summary>The printer's name in <c>Spark:Printing:Printers</c>.</summary>
    public string Printer { get; set; } = "";

    /// <summary>The printer's adapter when queued (copied: routing and the index need no configuration lookup).</summary>
    public string Adapter { get; set; } = "";

    /// <summary>For agent printers: the agent that delivers it.</summary>
    public string? Agent { get; set; }

    /// <summary>For agent printers: the raw port, <c>host:9100</c>.</summary>
    public string? Address { get; set; }

    public string? Site { get; set; }

    public string? Station { get; set; }

    /// <summary>The label kind (<see cref="LabelKinds"/>), for routing and grids.</summary>
    public string Kind { get; set; } = "";

    /// <summary>A short human description: "SSCC 376…17", "Location A-01-03".</summary>
    public string Title { get; set; } = "";

    /// <summary><c>application/zpl</c> or <c>application/pdf</c>.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>For ZPL: 203 or 300.</summary>
    public int? Dpi { get; set; }

    /// <summary>The bytes to print (ZPL as UTF-8 text, or a PDF).</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>A PDF of the same label, offered at the station when the agent is down (WD7), or null when there is none.</summary>
    public byte[]? FallbackPdf { get; set; }

    public PrintJobState State { get; set; }

    /// <summary>How many times it was delivered (a redelivery after a lost acknowledgement counts).</summary>
    public int Deliveries { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset? PrintedAt { get; set; }

    /// <summary>How it was printed in the end: the adapter, or <c>PdfFallback</c>.</summary>
    public string? PrintedVia { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>The job this one reprints.</summary>
    public string? ReprintOf { get; set; }

    /// <summary>When the station took the PDF fallback (the agent then never gets it).</summary>
    public DateTimeOffset? FallbackTakenAt { get; set; }

    /// <summary>Set when the job is printed or failed: kept for <c>Spark:Printing:Retention</c>, then removed.</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>
/// An agent's last report (Q-W14, monitoring): <c>print-agents/{name}</c>. The app shows its version and heartbeat and warns
/// when it is outdated or offline.
/// </summary>
public sealed class PrintAgentStatus
{
    public const string IdPrefix = "print-agents/";

    public static string IdFor(string agent) => IdPrefix + Uri.EscapeDataString(agent.Trim().ToLowerInvariant());

    public string? Id { get; set; }

    public string Name { get; set; } = "";

    public string? Version { get; set; }

    public string? MachineName { get; set; }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>Printers the agent could not reach at its last heartbeat.</summary>
    public List<string> UnreachablePrinters { get; set; } = [];

    /// <summary>Jobs the agent cannot say whether it printed (it stopped between sending and recording): never reprinted on its own.</summary>
    public List<string> UncertainJobs { get; set; } = [];
}

/// <summary>A scale's last reading at a station (PRD §4.7, Packing step 4: weigh through the agent): <c>print-scales/{station}</c>.</summary>
public sealed class ScaleReading
{
    public const string IdPrefix = "print-scales/";

    public static string IdFor(string station) => IdPrefix + Uri.EscapeDataString(station.Trim().ToLowerInvariant());

    public string? Id { get; set; }

    public string Station { get; set; } = "";

    public string Agent { get; set; } = "";

    public decimal Grams { get; set; }

    /// <summary>Whether the scale reported a stable weight.</summary>
    public bool Stable { get; set; }

    public DateTimeOffset ReadAt { get; set; }
}
