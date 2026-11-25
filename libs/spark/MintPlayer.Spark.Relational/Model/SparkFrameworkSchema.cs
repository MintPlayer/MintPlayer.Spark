using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MintPlayer.Spark.Relational.Model;

/// <summary>
/// The store's own tables (#536 D24): reservations (S6), atomic values (S15) and the Data Protection key ring, in the
/// <c>spark</c> schema, versioned apart from the app's tables by Spark's hand-written migrations
/// (<see cref="SparkFrameworkDbContext"/>, history <c>spark.__SparkFrameworkMigrations</c>). The app's
/// <see cref="SparkDbContext"/> maps the same tables to use them, excluded from the app's migrations.
/// </summary>
public static class SparkFrameworkSchema
{
    public const string Schema = "spark";
    public const string HistoryTable = "__SparkFrameworkMigrations";
    public const string Reservations = "Reservations";
    public const string AtomicValues = "AtomicValues";
    public const string DataProtectionKeys = "DataProtectionKeys";
    internal const int KeyLength = 450;

    /// <summary>Maps the framework tables. Keys compare bytes, as ids do (D7).</summary>
    internal static void Map(ModelBuilder modelBuilder, string keyCollation, bool excludeFromMigrations)
    {
        var reservation = modelBuilder.Entity<SparkReservationRow>();
        reservation.ToTable(Reservations, Schema, t => t.ExcludeFromMigrations(excludeFromMigrations));
        reservation.HasKey(r => r.Key);
        reservation.Property(r => r.Key).HasMaxLength(KeyLength).UseCollation(keyCollation);
        reservation.Property(r => r.OwnerId).HasMaxLength(KeyLength).UseCollation(keyCollation);
        reservation.Property(r => r.Version).IsConcurrencyToken();

        var atomic = modelBuilder.Entity<SparkAtomicValueRow>();
        atomic.ToTable(AtomicValues, Schema, t => t.ExcludeFromMigrations(excludeFromMigrations));
        atomic.HasKey(r => r.Key);
        atomic.Property(r => r.Key).HasMaxLength(KeyLength).UseCollation(keyCollation);
        atomic.Property(r => r.Version).IsConcurrencyToken();

        var key = modelBuilder.Entity<SparkDataProtectionKeyRow>();
        key.ToTable(DataProtectionKeys, Schema, t => t.ExcludeFromMigrations(excludeFromMigrations));
        key.HasKey(r => r.Name);
        key.Property(r => r.Name).HasMaxLength(KeyLength).UseCollation(keyCollation);
    }
}

/// <summary>
/// The context that migrates the framework schema (D24, D33): applied before the app's migrations, with a history
/// table of its own. Its migrations are hand-written and ship in this package (<c>Migrations/Framework</c>).
/// </summary>
public sealed class SparkFrameworkDbContext : DbContext
{
    public SparkFrameworkDbContext(DbContextOptions<SparkFrameworkDbContext> options, SparkRelationalDialect dialect) : base(options)
        => Dialect = dialect;

    public SparkRelationalDialect Dialect { get; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => SparkFrameworkSchema.Map(modelBuilder, Dialect.KeyCollation, excludeFromMigrations: false);

    /// <summary>
    /// The framework's migrations are written by hand, with no model snapshot, so EF's pending-changes check has
    /// nothing to compare with. <c>SparkFrameworkSchemaTests</c> checks the migrations against the model instead.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder
            .ReplaceService<IModelCacheKeyFactory, FrameworkModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    private sealed class FrameworkModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context.GetType(), ((SparkFrameworkDbContext)context).Dialect.GetType(), designTime);
    }
}
