using MintPlayer.Spark.Abstractions;

namespace SeoTester.LookupReferences;

// The choice lists of SeoTester's documents. Keys are stored (strings, the member names); the labels are the
// translations.json keys lookup.{Enum}.{Member} (commerce X20 item 5). The check categories and severities mirror
// SeoTester.Catalogue's enums, by name.

/// <summary>A site's scheduled monitoring (D12): off, weekly or monthly. Gated by <c>SeoTester:Features:Monitoring</c>.</summary>
[SparkLookup(StringKeys = true)]
public enum EMonitoringSchedule
{
    /// <summary>No scheduled runs.</summary>
    Off,
    /// <summary>A run every week.</summary>
    Weekly,
    /// <summary>A run every month.</summary>
    Monthly,
}

/// <summary>How ownership of a site was proven (F3, D29).</summary>
[SparkLookup(StringKeys = true)]
public enum EVerificationMethod
{
    /// <summary>A TXT record at _seotester.&lt;host&gt;.</summary>
    Dns,
    /// <summary>A meta tag on the home page.</summary>
    MetaTag,
    /// <summary>A file at /.well-known/seotester-verification.txt.</summary>
    WellKnownFile,
}

/// <summary>Where an audit run is.</summary>
[SparkLookup(StringKeys = true)]
public enum EAuditRunStatus
{
    /// <summary>Waiting for the crawler.</summary>
    Queued,
    /// <summary>Being crawled.</summary>
    Running,
    /// <summary>Finished and scored.</summary>
    Completed,
    /// <summary>Stopped on request.</summary>
    Cancelled,
    /// <summary>Stopped by an error.</summary>
    Failed,
    /// <summary>robots.txt refused the crawler (the site opted out, disallows everyone, or robots.txt was unreachable): nothing was crawled (D19).</summary>
    Blocked,
}

public sealed partial class AuditRunStatus
{
    /// <summary>A run in one of these is over.</summary>
    public static readonly IReadOnlyList<string> Finished = [Completed, Cancelled, Failed, Blocked];

    /// <summary>A run in one of these can still be joined (D17).</summary>
    public static readonly IReadOnlyList<string> InProgress = [Queued, Running];
}

/// <summary>The catalogue's categories (SeoTester.Catalogue.CheckCategory).</summary>
[SparkLookup(StringKeys = true)]
public enum ECheckCategoryLookup
{
    /// <summary>On-page SEO.</summary>
    Seo,
    /// <summary>Technical and crawlability.</summary>
    Technical,
    /// <summary>Speed and weight.</summary>
    Performance,
    /// <summary>Transport and headers.</summary>
    Security,
    /// <summary>Accessibility.</summary>
    Accessibility,
    /// <summary>Social previews.</summary>
    Social,
    /// <summary>Mobile and PWA.</summary>
    Mobile,
    /// <summary>Domain and DNS (information).</summary>
    Domain,
}

/// <summary>The catalogue's severities (SeoTester.Catalogue.CheckSeverity).</summary>
[SparkLookup(StringKeys = true)]
public enum ECheckSeverityLookup
{
    /// <summary>Caps its category when it fails.</summary>
    Critical,
    /// <summary>High.</summary>
    High,
    /// <summary>Medium.</summary>
    Medium,
    /// <summary>Low.</summary>
    Low,
    /// <summary>Shown, not scored.</summary>
    Info,
}
