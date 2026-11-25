using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// <see cref="ISparkStore"/> over a relational database (#536 D1, D2): one <see cref="SparkDbContext"/> per session.
/// Reads see every committed write, so there is never anything to wait for.
/// </summary>
public sealed class RelationalSparkStore : ISparkStore
{
    /// <summary>The app's migrations are recorded in EF's usual history table.</summary>
    public const string AppHistoryTable = "__EFMigrationsHistory";

    private readonly DbContextOptions<SparkDbContext> options;
    private readonly DbContextOptions<SparkFrameworkDbContext> frameworkOptions;

    /// <param name="setup">The model, dialect and hooks.</param>
    /// <param name="connectionString">The database.</param>
    /// <param name="configure">The app's context options (logging, interceptors), applied to both contexts.</param>
    /// <param name="migrationsAssembly">
    /// The assembly that holds the app's migrations (D33), which synchronize writes; <see langword="null"/> for none, as
    /// in tests, whose schema comes from the model (<see cref="RelationalSchema.CreateFromModelAsync"/>).
    /// </param>
    public RelationalSparkStore(SparkContextSetup setup, string connectionString, Action<DbContextOptionsBuilder>? configure = null, string? migrationsAssembly = null)
    {
        Setup = setup;
        ConnectionString = connectionString;
        MigrationsAssembly = migrationsAssembly;
        SparkRelationalDialect.Register(setup.Dialect);

        var builder = new DbContextOptionsBuilder<SparkDbContext>();
        setup.Dialect.UseProvider(builder, connectionString, new SparkMigrationsSettings(migrationsAssembly ?? typeof(RelationalSparkStore).Assembly.GetName().Name, AppHistoryTable, null));
        configure?.Invoke(builder);
        options = builder.Options;

        var framework = new DbContextOptionsBuilder<SparkFrameworkDbContext>();
        setup.Dialect.UseProvider(framework, connectionString, new SparkMigrationsSettings(
            typeof(SparkFrameworkDbContext).Assembly.GetName().Name, SparkFrameworkSchema.HistoryTable, SparkFrameworkSchema.Schema));
        configure?.Invoke(framework);
        frameworkOptions = framework.Options;

        // D32: EF writes each view's SQL from a model without it (no connection is opened); the model with the SQL is
        // the one every context uses from here on, and the one synchronize compares with the snapshot.
        if (setup.RenderedViews is null)
        {
            using var render = NewContext();
            setup.RenderedViews = RelationalViewRenderer.RenderAll(render);
        }
    }

    public SparkContextSetup Setup { get; }

    /// <summary>The database's connection string.</summary>
    public string ConnectionString { get; }

    /// <summary>The assembly holding the app's migrations, or <see langword="null"/> when the app has none (tests).</summary>
    public string? MigrationsAssembly { get; }

    /// <summary>A new context over the framework schema (D24); the caller disposes it.</summary>
    public SparkFrameworkDbContext NewFrameworkContext() => new(frameworkOptions, Setup.Dialect);

    public SparkRelationalModel Model => Setup.Model;

    public string ProviderName => Setup.Dialect.ProviderName;

    /// <summary>D6: the change feed (<see cref="RelationalSparkChangeFeed"/>) over the stamps and the tombstones.</summary>
    public SparkStoreCapabilities Capabilities => SparkStoreCapabilities.ChangeNotifications | SparkStoreCapabilities.Subscriptions;

    /// <summary>Raised after a session's commit with what it wrote (<see cref="ISparkChangeFeed.OnCommitted"/>).</summary>
    internal event Action<IReadOnlyList<SparkStoreChange>>? Committed;

    internal void RaiseCommitted(IReadOnlyList<SparkStoreChange> changes)
    {
        if (changes.Count == 0 || Committed is not { } committed)
            return;
        foreach (var handler in committed.GetInvocationList().Cast<Action<IReadOnlyList<SparkStoreChange>>>())
        {
            try
            {
                handler(changes);
            }
            catch
            {
                // A handler never throws into a writer (ISparkChangeFeed).
            }
        }
    }

    /// <summary>A new context over the store's database; the caller disposes it.</summary>
    public SparkDbContext NewContext() => new(options, Setup);

    public ISparkSession OpenSession() => new RelationalSparkSession(NewContext(), this);

    private volatile Action<object, bool, Action<DateTimeOffset?>>[] beforeSave = [];

    /// <summary>
    /// The store's own save hooks (the expiration stamper): called for every new or changed row with the entity, whether
    /// it is new, and a setter for its expiry column. Dispose the result to remove the hook.
    /// </summary>
    internal IDisposable AddBeforeSave(Action<object, bool, Action<DateTimeOffset?>> hook)
    {
        lock (this)
            beforeSave = [.. beforeSave, hook];
        return new Removal(() =>
        {
            lock (this)
                beforeSave = [.. beforeSave.Where(h => h != hook)];
        });
    }

    internal IReadOnlyList<Action<object, bool, Action<DateTimeOffset?>>> BeforeSaveHooks => beforeSave;

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? remove = remove;

        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }

    public string GetCollectionName(Type type) => Model.TableOf(type)?.Name ?? SparkCollectionNames.Of(type);

    public Task<bool> WaitForConsistencyAsync(
        ISparkSession? session, IReadOnlyCollection<string> collections, TimeSpan bound,
        Action<Exception, string, TimeSpan>? onUnfinished, CancellationToken cancellationToken)
        => Task.FromResult(true);
}
