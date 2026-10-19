using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.ReferenceData;

/// <summary><c>Spark:ReferenceData</c> (PRD §3.3). Code given to <c>AddReferenceData(o =&gt; …)</c> runs after the section.</summary>
public sealed class SparkReferenceDataOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:ReferenceData";

    /// <summary>Per source id (<c>be-refnis</c>): its schedule and whether it runs (RD1).</summary>
    public Dictionary<string, ReferenceDataSourceOptions> Sources { get; set; } = new(StringComparer.Ordinal);

    public ReferenceDataApiOptions Api { get; set; } = new();

    public ReferenceDataReleaseOptions Releases { get; set; } = new();

    public ReferenceDataSeedOptions Seed { get; set; } = new();

    public ReferenceDataImportOptions Import { get; set; } = new();
}

public sealed class ReferenceDataSourceOptions
{
    /// <summary>A 6-field cron (UTC). Empty = the adapter's default.</summary>
    public string? Schedule { get; set; }

    /// <summary>Scheduled checks run only for enabled sources. Default <see langword="false"/>: full imports are manual local runs (RD2).</summary>
    public bool Enabled { get; set; }
}

/// <summary>The public API's caps (§4.11). Proposed defaults, configurable.</summary>
public sealed class ReferenceDataApiOptions
{
    /// <summary>The longest query, in characters after folding.</summary>
    public int MaxQueryLength { get; set; } = 64;

    public int MaxTokens { get; set; } = 5;

    public int DefaultLimit { get; set; } = 10;

    public int MaxLimit { get; set; } = 20;

    /// <summary>The fuzzy step runs only when the prefix step returns fewer than this many results (RD8).</summary>
    public int FuzzyBelow { get; set; } = 5;

    /// <summary>A query's own time limit, beside <c>RequestAborted</c>.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The token bucket of the <c>/api/v1</c> rate-limit policy, per IPv4 address or IPv6 /64.</summary>
    public int TokenLimit { get; set; } = 60;

    public int TokensPerPeriod { get; set; } = 20;

    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary><c>Cache-Control: public, max-age</c> of suggestions.</summary>
    public TimeSpan CacheMaxAge { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class ReferenceDataReleaseOptions
{
    /// <summary>Where release files are written (Q-R8's file-system default). Relative paths are under the content root.</summary>
    public string Directory { get; set; } = "RefDataReleaseStore";

    /// <summary>Sign manifests (RD-DEF 5). Needs an <c>IReleaseSigner</c>; the algorithm choice is spike RS17.</summary>
    public bool Sign { get; set; }

    /// <summary>A release dropping more than this share of a collection's stored ids is refused as a broken download (§4.5 step 8).</summary>
    public double MaxRetiredShare { get; set; } = 0.2;
}

public sealed class ReferenceDataSeedOptions
{
    /// <summary>Apply the committed basic seed set on start (RD9). Default on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The seed release directory. Relative paths are under the content root.</summary>
    public string Directory { get; set; } = "Seed";
}

public sealed class ReferenceDataImportOptions
{
    /// <summary>The import queue's handler timeout (a full Belgian import is long; RD1 step 3).</summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>The download cap per file.</summary>
    public long MaxDownloadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
