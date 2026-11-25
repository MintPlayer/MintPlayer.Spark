using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Design;
using MintPlayer.Spark.Relational.Storage;

[assembly: SparkSchemaScaffolder(typeof(RelationalSchemaScaffolder))]

namespace MintPlayer.Spark.Relational.Design;

/// <summary>
/// Writes the EF migration for a Spark model change (#536 D24, S3): the store's <see cref="Model.SparkDbContext"/> is
/// compared with the app's last snapshot through EF's public design-time services, with no <c>dotnet ef</c> and no
/// design-time context factory. The migration goes to <c>Migrations/&lt;Provider&gt;</c> in the host project.
/// </summary>
public sealed class RelationalSchemaScaffolder : ISparkSchemaScaffolder
{
    public const string DefaultName = "SparkModel";

    public Task<SparkScaffoldResult> ScaffoldAsync(IServiceProvider services, SparkScaffoldRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(request);
        var store = services.GetRequiredService<RelationalSparkStore>();
        return Task.FromResult(Scaffold(store, request));
    }

    /// <summary>The same, for a store built by hand (tests).</summary>
    public static SparkScaffoldResult Scaffold(RelationalSparkStore store, SparkScaffoldRequest request)
    {
        using var db = store.NewContext();
        if (!db.Database.HasPendingModelChanges())
            return SparkScaffoldResult.NoChanges;

        var reporter = new CollectingReporter();
        using var designServices = DesignServices(db, reporter);

        var operations = Differences(designServices, db);
        RefuseRecreatedTables(designServices, operations);

        var provider = store.Setup.Dialect.ProviderName;
        var rootNamespace = store.MigrationsAssembly ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "App";
        var scaffolder = designServices.GetRequiredService<IMigrationsScaffolder>();
        var migration = scaffolder.ScaffoldMigration(request.Name ?? DefaultName, rootNamespace, $"Migrations.{provider}", "C#");
        var files = scaffolder.Save(request.ProjectDirectory, migration, Path.Combine(request.ProjectDirectory, "Migrations", provider));

        return new SparkScaffoldResult(
            [.. new[] { files.MigrationFile, files.MetadataFile, files.SnapshotFile }.OfType<string>()],
            [.. operations.Select(Describe)],
            reporter.Warnings);
    }

    /// <summary>
    /// EF's design-time services for <paramref name="db"/>, composed from public API (S3 finding 1). The provider's own
    /// design-time services are found through the attribute on its assembly, as the EF tools find them.
    /// </summary>
    private static ServiceProvider DesignServices(DbContext db, IOperationReporter reporter)
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices(reporter);
        services.AddDbContextDesignTimeServices(db);

        var providerAssembly = Assembly.Load(new AssemblyName(db.Database.ProviderName!));
        var attribute = providerAssembly.GetCustomAttribute<DesignTimeProviderServicesAttribute>()
            ?? throw new InvalidOperationException($"{providerAssembly.GetName().Name} declares no design-time services.");
        var designTime = (IDesignTimeServices)Activator.CreateInstance(providerAssembly.GetType(attribute.TypeName, throwOnError: true)!)!;
        designTime.ConfigureDesignTimeServices(services);

        // dotnet/efcore#36016 (S3 finding 2): HasPendingModelChanges finalized the context's snapshot, and the scaffolder
        // would read it as "no previous model" and recreate every table. It goes once dotnet/efcore#39784 ships.
        services.Replace(ServiceDescriptor.Singleton<IMigrationsAssembly>(new FreshSnapshotMigrationsAssembly(db.GetService<IMigrationsAssembly>())));
        return services.BuildServiceProvider();
    }

    /// <summary>The operations the migration will hold: the last snapshot against the model, through Spark's differ.</summary>
    private static IReadOnlyList<MigrationOperation> Differences(IServiceProvider designServices, DbContext db)
        => db.GetService<IMigrationsModelDiffer>().GetDifferences(
            PreviousModel(designServices)?.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

    private static IModel? PreviousModel(IServiceProvider designServices)
        => designServices.GetRequiredService<IMigrationsAssembly>().ModelSnapshot?.Model is { } snapshot
            ? designServices.GetRequiredService<ISnapshotModelProcessor>().Process(snapshot)
            : null;

    /// <summary>
    /// The guard D24 keeps until the EF fix ships: a migration that creates a table the last snapshot already has means
    /// the comparison ran against no model, and applying it would fail or lose data.
    /// </summary>
    private static void RefuseRecreatedTables(IServiceProvider designServices, IReadOnlyList<MigrationOperation> operations)
    {
        if (PreviousModel(designServices)?.GetRelationalModel() is not { } previous)
            return;
        var recreated = operations.OfType<CreateTableOperation>()
            .Where(create => previous.FindTable(create.Name, create.Schema) is not null)
            .Select(create => create.Name)
            .ToList();
        if (recreated.Count > 0)
            throw new InvalidOperationException(
                $"Spark refused the migration: it creates {string.Join(", ", recreated)}, which the last migration's snapshot already has. "
                + "The comparison ran against no previous model (dotnet/efcore#36016); nothing was written.");
    }

    private static string Describe(MigrationOperation operation)
    {
        var kind = operation.GetType().Name.Replace("Operation", "", StringComparison.Ordinal);
        return operation switch
        {
            CreateTableOperation create => $"{kind} {create.Name}",
            DropTableOperation dropTable => $"{kind} {dropTable.Name}",
            ColumnOperation column => $"{kind} {column.Table}.{column.Name}",
            RenameColumnOperation rename => $"{kind} {rename.Table}.{rename.Name} -> {rename.NewName}",
            DropColumnOperation drop => $"{kind} {drop.Table}.{drop.Name}",
            CreateIndexOperation index => $"{kind} {index.Name} on {index.Table}",
            ITableMigrationOperation table => $"{kind} on {table.Table}",
            _ => kind,
        };
    }

    /// <summary>A new instance of the snapshot class per scaffold: a new, mutable model (dotnet/efcore#36016).</summary>
    private sealed class FreshSnapshotMigrationsAssembly : IMigrationsAssembly
    {
        private readonly IMigrationsAssembly inner;
        private readonly Lazy<ModelSnapshot?> snapshot;

        public FreshSnapshotMigrationsAssembly(IMigrationsAssembly inner)
        {
            this.inner = inner;
            snapshot = new(() => inner.ModelSnapshot is { } shared ? (ModelSnapshot)Activator.CreateInstance(shared.GetType())! : null);
        }

        public ModelSnapshot? ModelSnapshot => snapshot.Value;
        public IReadOnlyDictionary<string, TypeInfo> Migrations => inner.Migrations;
        public Assembly Assembly => inner.Assembly;
        public string? FindMigrationId(string nameOrId) => inner.FindMigrationId(nameOrId);
        public Migration CreateMigration(TypeInfo migrationClass, string activeProvider) => inner.CreateMigration(migrationClass, activeProvider);
        public void AddMigrations(Assembly additionalMigrationsAssembly) => inner.AddMigrations(additionalMigrationsAssembly);
    }

    private sealed class CollectingReporter : IOperationReporter
    {
        public List<string> Warnings { get; } = [];
        public void WriteError(string message) => Warnings.Add("error: " + message);
        public void WriteWarning(string message) => Warnings.Add(message);
        public void WriteInformation(string message) { }
        public void WriteVerbose(string message) { }
    }
}
