namespace MintPlayer.Spark.Services;

/// <summary>What a reload that does not compose leaves in place (composition D8).</summary>
internal enum AppLayerFailure
{
    /// <summary>The previous snapshot stays, and the failure is logged: a half-saved edit does not take the running application's texts or menu away.</summary>
    KeepPrevious,

    /// <summary>
    /// Every read throws the failure until a reload composes again: fail closed. Rights only, where the
    /// previous snapshot is no longer what the file says and keeping it would grant what was removed.
    /// </summary>
    Refuse,
}

/// <summary>
/// The one watcher policy for an application layer (composition D8): a snapshot composed on first
/// read, recomposed when one of the watched <c>App_Data</c> files changes (every event, debounced) or
/// at once when the translations it resolves labels against reload, and swapped atomically.
/// </summary>
/// <remarks>
/// <para>
/// The first read composes and throws, so a broken file stops startup. A reader sees the old snapshot
/// or the new one, never a mix. What a reload that does not compose leaves in place is the kind's
/// (<see cref="AppLayerFailure"/>). Library layers are compiled in and never reload (D15); only the
/// application's files are watched.
/// </para>
/// <para>
/// Debounced: an editor's save raises several events (a write, a size change, a rename through a
/// temporary file), and each restarts the wait, so one save composes once.
/// </para>
/// </remarks>
internal sealed class AppLayerSnapshot<T> : IDisposable where T : class
{
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(100);

    private readonly string what;
    private readonly string? directory;
    private readonly IReadOnlyCollection<string> fileNames;
    private readonly Func<T> compose;
    private readonly ILogger logger;
    private readonly AppLayerFailure onFailure;
    private readonly ITranslationsLoader? labels;
    private readonly TimeSpan debounce;
    private readonly object gate = new();

    private volatile T? snapshot;
    private volatile Exception? refusal;
    private FileSystemWatcher? watcher;
    private Timer? timer;
    private bool started;
    private bool disposed;

    /// <param name="what">The layer as log lines name it, e.g. <c>App_Data/actions.json</c>.</param>
    /// <param name="directory">The directory the watched files are in; <see langword="null"/>: no file is watched.</param>
    /// <param name="fileNames">The watched file names in <paramref name="directory"/>, ignoring case.</param>
    /// <param name="compose">Composes the library layers with the application's files; throws when they do not compose.</param>
    /// <param name="labels">The translations the snapshot resolves labels against: their reload recomposes it too.</param>
    public AppLayerSnapshot(
        string what,
        string? directory,
        IReadOnlyCollection<string> fileNames,
        Func<T> compose,
        ILogger logger,
        AppLayerFailure onFailure = AppLayerFailure.KeepPrevious,
        ITranslationsLoader? labels = null,
        TimeSpan? debounce = null)
    {
        this.what = what;
        this.directory = directory;
        this.fileNames = fileNames;
        this.compose = compose;
        this.logger = logger;
        this.onFailure = onFailure;
        this.labels = labels;
        this.debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>Raised after a reload replaced the snapshot (never for the first composition).</summary>
    public event Action? Reloaded;

    /// <summary>The snapshot; the first read composes it and starts watching.</summary>
    /// <exception cref="Exception">The first composition failed, or (<see cref="AppLayerFailure.Refuse"/>) the last reload did.</exception>
    public T Current
    {
        get
        {
            if (refusal is { } failure) throw failure;
            if (snapshot is { } current) return current;

            lock (gate)
            {
                if (refusal is { } refused) throw refused;
                if (snapshot is null)
                {
                    snapshot = compose();
                    Start();
                }
                return snapshot;
            }
        }
    }

    /// <summary>Composes again and swaps the snapshot; on failure applies the kind's <see cref="AppLayerFailure"/>.</summary>
    /// <returns>Whether the snapshot was replaced.</returns>
    public bool Reload()
    {
        bool replaced;
        lock (gate)
        {
            if (disposed) return false;
            try
            {
                snapshot = compose();
                refusal = null;
                replaced = true;
                Start();
            }
            catch (Exception ex)
            {
                if (onFailure == AppLayerFailure.Refuse)
                {
                    refusal = ex;
                    logger.LogError(ex, "Failed to reload {Layer}; it is refused until it composes again", what);
                }
                else
                {
                    logger.LogError(ex, "Failed to reload {Layer}; the previous one stays in use", what);
                }
                replaced = false;
            }
        }

        if (replaced)
        {
            logger.LogInformation("Reloaded {Layer}", what);
            Reloaded?.Invoke();
        }
        return replaced;
    }

    /// <summary>What a watched file's change does: (re)starts the debounce wait, after which the layer reloads once.</summary>
    internal void Signal()
    {
        lock (gate)
        {
            if (disposed) return;
            timer ??= new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Start()
    {
        if (started || disposed) return;
        started = true;

        if (labels is not null)
            labels.Reloaded += OnLabelsReloaded;

        if (string.IsNullOrEmpty(directory) || fileNames.Count == 0 || !Directory.Exists(directory))
            return;

        watcher = new FileSystemWatcher(directory)
        {
            // Created, Deleted and Renamed too: an editor that saves through a temporary file replaces
            // the file rather than writing it, and an application without one may add it while running.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += OnFileEvent;
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// The translations already waited out their own save, so the labels recompose at once: when the
    /// translations' reload returns, every label resolved against them has followed (M5's leftover).
    /// </summary>
    private void OnLabelsReloaded()
    {
        // Not composed yet: the first read resolves against the new translations anyway.
        if (snapshot is not null) Reload();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs args)
    {
        if (Watches(args.Name) || (args is RenamedEventArgs renamed && Watches(renamed.OldName)))
            Signal();
    }

    private bool Watches(string? name)
        => name is not null && fileNames.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            if (labels is not null && started)
                labels.Reloaded -= OnLabelsReloaded;

            if (watcher is not null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnFileEvent;
                watcher.Created -= OnFileEvent;
                watcher.Deleted -= OnFileEvent;
                watcher.Renamed -= OnFileEvent;
                watcher.Dispose();
                watcher = null;
            }

            timer?.Dispose();
            timer = null;
        }
    }
}
