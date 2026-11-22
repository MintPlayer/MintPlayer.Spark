namespace MintPlayer.Spark.Files;

/// <summary>
/// The Files plugin's settings, bound from <c>Spark:Files</c> and then from <c>AddFiles(o =&gt; …)</c> (code wins).
/// An attribute's <c>fileOptions</c> can only tighten these (#700 D16).
/// </summary>
public sealed class SparkFilesOptions
{
    public const string SectionName = "Spark:Files";

    /// <summary>The global ceiling per file. An attribute's <c>maxBytes</c> above it is refused at startup. Default 10 MB.</summary>
    public long MaxFileBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>The <c>maxCount</c> of a list attribute that declares none. Default 10 (D9).</summary>
    public int DefaultMaxCount { get; set; } = 10;

    /// <summary>The most files one prepare may ask for. Default 20.</summary>
    public int MaxBatch { get; set; } = 20;

    /// <summary>How long an unclaimed upload lives. Default 24 h. Its bytes go with it on every store.</summary>
    public TimeSpan StagingLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a prepared upload slot accepts bytes. Default 15 min.</summary>
    public TimeSpan UploadWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Prepared files per user per minute. Default 30. 0 turns the limit off.</summary>
    public int UploadsPerMinute { get; set; } = 30;

    /// <summary>
    /// How often a store outside RavenDB (<see cref="Storage.ObjectFileStore"/>) is swept for objects no entity or upload
    /// holds any more: left by a crash or a failed delete between the commit and the store. Default 1 h; zero turns it off.
    /// </summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How old an unheld object must be before the sweep deletes it. It covers a save in flight: its entity's copy exists
    /// before the entity commits. Default 1 h.
    /// </summary>
    public TimeSpan ReconcileGrace { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Starts in Production without an <c>IFileScanner</c>, and logs a warning on every start (D5). Every file is then
    /// treated as <c>Clean</c>. Off by default: Production without a scanner refuses to start.
    /// </summary>
    public bool AllowUnscannedFiles { get; set; }

    /// <summary>When the registered scanner runs (D6): during the commit (default) or in a background worker.</summary>
    public Scanning.SparkFileScanMode ScanMode { get; set; } = Scanning.SparkFileScanMode.Inline;

    /// <summary>
    /// How long one scan may take before it counts as failed. Default 2 min. A failed scan is never a pass: the file stays
    /// unclaimable and the next commit (or, deferred, the next sweep) retries it.
    /// </summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Deferred scanning only: how often the worker looks for files still <c>Pending</c> or <c>ScanFailed</c> (after a
    /// restart, or a scanner that was down). Default 1 min.
    /// </summary>
    public TimeSpan ScanSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long the URL a checked download redirects to stays valid (D14). Default 60 s. Revoking a right takes effect for a
    /// file only once the URLs already issued for it expire.
    /// </summary>
    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the URLs embedded in read responses stay valid (D15): images, their variants, and files inside AsDetail rows.
    /// Default 5 min. A page kept open longer fetches the object again for fresh ones.
    /// </summary>
    public TimeSpan EmbeddedUrlLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A separate, cookie-less origin (<c>https://files.example.com</c>) that serves proxied bytes (D8). It must route
    /// <c>/spark/files/content/</c> to this app. Optional: by default the app's own origin serves them, sandboxed.
    /// </summary>
    public string? ContentOrigin { get; set; }

    /// <summary>
    /// The largest image accepted, in pixels (width × height), read from the header before anything is decoded: the
    /// decompression-bomb guard (PRD SP5). Default 40 million. An attribute's <c>fileOptions.maxPixels</c> can only tighten it.
    /// </summary>
    public long MaxImagePixels { get; set; } = 40_000_000;

    /// <summary>
    /// How many images one instance decodes at once. A decoded image at the pixel cap is about 160 MB (4 bytes a pixel), so
    /// this bounds what concurrent commits can make the process allocate. Further commits wait their turn. Default 2.
    /// </summary>
    public int MaxConcurrentImageProcessing { get; set; } = 2;

    /// <summary>
    /// The variants of an <c>Image</c> attribute whose <c>fileOptions</c> declare none: name → longest edge in pixels.
    /// Default <c>thumb</c> 160 and <c>medium</c> 800. An attribute's empty <c>variants</c> object turns them off.
    /// </summary>
    public Dictionary<string, int> DefaultImageVariants { get; set; } = new(StringComparer.Ordinal) { ["thumb"] = 160, ["medium"] = 800 };
}
