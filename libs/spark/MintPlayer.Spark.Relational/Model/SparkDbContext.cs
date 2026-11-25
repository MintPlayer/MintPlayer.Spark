using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Relational.Model;

/// <summary>What a <see cref="SparkDbContext"/> is built from: the relational model, the dialect and the app's hooks.</summary>
public sealed class SparkContextSetup
{
    internal SparkContextSetup(SparkRelationalModel model, SparkRelationalDialect dialect, IReadOnlyList<Action<ModelBuilder>> configureModel)
    {
        Model = model;
        Dialect = dialect;
        ConfigureModel = configureModel;
    }

    public SparkRelationalModel Model { get; }

    public SparkRelationalDialect Dialect { get; }

    /// <summary>The app's <c>ConfigureModel</c> hooks (O9), run last.</summary>
    public IReadOnlyList<Action<ModelBuilder>> ConfigureModel { get; }
}

/// <summary>
/// The <see cref="DbContext"/> Spark builds from its model (#536 D1): no <c>DbSet</c> properties and no EF attributes on
/// the entities, so a feature library ships its entities with no EF code.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>One table per collection, TPH for stored derived types; the key column per <see cref="SparkKeyShape"/>.</item>
/// <item>D3: a <c>Version</c> bigint concurrency token on every table, moved by the session.</item>
/// <item>D7: text keys and the FK columns pointing at them compare bytes (the dialect's binary collation).</item>
/// <item>D25: a <c>[ValueObject]</c> collection is a child table keyed by (owner id, row key), deleted with its owner.</item>
/// <item>D26: an id property is an FK column, a reference list a join table; neither is an EF navigation.</item>
/// <item>D1: a value without a relational shape is a JSON column.</item>
/// </list>
/// </remarks>
public sealed class SparkDbContext : DbContext
{
    public const string VersionProperty = "Version";
    public const string OwnerIdProperty = "OwnerId";
    public const string TargetIdProperty = "TargetId";

    /// <summary>S6 <c>SetExpiry</c>: when the expiration sweeper may delete the row (UTC), on every table.</summary>
    public const string ExpiresProperty = "SparkExpiresAt";

    public const string ReservationsTable = "SparkReservations";
    public const string AtomicValuesTable = "SparkAtomicValues";
    public const string DataProtectionKeysTable = "SparkDataProtectionKeys";
    internal const int KeyLength = 450;

    public SparkDbContext(DbContextOptions<SparkDbContext> options, SparkContextSetup setup) : base(options) => Setup = setup;

    public SparkContextSetup Setup { get; }

    public SparkRelationalModel SparkModel => Setup.Model;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var model = Setup.Model;
        foreach (var table in model.Tables)
        {
            var entity = modelBuilder.Entity(table.Root);
            entity.ToTable(table.Name);
            entity.HasKey(table.IdProperty.Name);
            var key = entity.Property(table.IdProperty.Name).ValueGeneratedNever();
            KeyColumn(key, table.IdProperty.PropertyType, table);
            entity.Property<long>(VersionProperty).IsConcurrencyToken();
            entity.Property<DateTimeOffset?>(ExpiresProperty);
            entity.HasIndex(ExpiresProperty);

            foreach (var derived in table.Derived)
                modelBuilder.Entity(derived).HasBaseType(BaseOf(derived, table));
            foreach (var type in table.Derived.Prepend(table.Root))
                MapMembers(modelBuilder.Entity(type), type, table);
        }

        foreach (var join in model.Joins)
            MapJoin(modelBuilder, join);

        MapFrameworkTables(modelBuilder);

        foreach (var configure in Setup.ConfigureModel)
            configure(modelBuilder);
    }

    /// <summary>The store's own tables: reservations (S6) and atomic values (S15). Keys compare bytes, as ids do (D7).</summary>
    private void MapFrameworkTables(ModelBuilder modelBuilder)
    {
        var reservation = modelBuilder.Entity<SparkReservationRow>();
        reservation.ToTable(ReservationsTable);
        reservation.HasKey(r => r.Key);
        reservation.Property(r => r.Key).HasMaxLength(KeyLength).UseCollation(Setup.Dialect.KeyCollation);
        reservation.Property(r => r.OwnerId).HasMaxLength(KeyLength).UseCollation(Setup.Dialect.KeyCollation);
        reservation.Property(r => r.Version).IsConcurrencyToken();

        var atomic = modelBuilder.Entity<SparkAtomicValueRow>();
        atomic.ToTable(AtomicValuesTable);
        atomic.HasKey(r => r.Key);
        atomic.Property(r => r.Key).HasMaxLength(KeyLength).UseCollation(Setup.Dialect.KeyCollation);
        atomic.Property(r => r.Version).IsConcurrencyToken();

        var key = modelBuilder.Entity<SparkDataProtectionKeyRow>();
        key.ToTable(DataProtectionKeysTable);
        key.HasKey(r => r.Name);
        key.Property(r => r.Name).HasMaxLength(KeyLength).UseCollation(Setup.Dialect.KeyCollation);
    }

    private Type BaseOf(Type derived, SparkTable table)
    {
        for (var t = derived.BaseType; t is not null; t = t.BaseType)
            if (t == table.Root || table.Derived.Contains(t))
                return t;
        return table.Root;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => Setup.Dialect.ConfigureConventions(configurationBuilder);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<IModelCacheKeyFactory, SparkModelCacheKeyFactory>();

    /// <summary>A key or FK column for ids of <paramref name="target"/>: its converter, and for text its length and collation.</summary>
    private void KeyColumn(PropertyBuilder property, Type propertyType, SparkTable target)
    {
        if (SparkIdConverters.For(propertyType, target) is { } converter)
            property.HasConversion(converter);
        if (target.Key is SparkKeyShape.Text or SparkKeyShape.Natural)
            property.HasMaxLength(KeyLength).UseCollation(Setup.Dialect.KeyCollation);
    }

    private void MapMembers(EntityTypeBuilder entity, Type type, SparkTable table)
    {
        foreach (var property in SparkRelationalModel.DeclaredStoredProperties(type))
        {
            if (type == table.Root && property.Name == table.IdProperty.Name)
                continue;
            if (Setup.Model.Joins.Any(j => j.Owner == type && j.Property == property))
            {
                entity.Ignore(property.Name);
                continue;
            }

            var element = SparkRelationalModel.CollectionElement(property.PropertyType);
            if (element is not null && element.GetCustomAttribute<ValueObjectAttribute>() is not null && IsMapped(property))
            {
                MapChildTable(entity, table, property, element);
                continue;
            }

            if (ReferenceTarget(property) is { } target && IsMapped(property))
            {
                var column = entity.Property(property.Name);
                if (Setup.Model.TableOf(target) is { } targetTable)
                {
                    KeyColumn(column, property.PropertyType, targetTable);
                    // An FK needs the same CLR type on both sides; a [Reference] string pointing at an Id<T> key stays a
                    // plain column with the key's converter (#536 D26, the string-reference deviation).
                    if ((Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == targetTable.IdProperty.PropertyType)
                        entity.HasOne(targetTable.Root).WithMany().HasForeignKey(property.Name).OnDelete(DeleteBehavior.Restrict);
                }
                else if (SparkIdConverters.ForUnstored(property.PropertyType) is { } converter)
                    column.HasConversion(converter);
                continue;
            }

            MapValue(property, entity.Ignore, name => entity.Property(name), type);
        }
    }

    /// <summary>The type an id property points at: <see cref="Id{T}"/>'s <c>T</c>, or a <c>[Reference]</c> string's target.</summary>
    private static Type? ReferenceTarget(PropertyInfo property)
        => SparkIdValues.TargetOf(property.PropertyType)
           ?? (property.PropertyType == typeof(string) ? property.GetCustomAttribute<ReferenceAttribute>()?.TargetType : null);

    /// <summary>
    /// A plain member: a column EF maps itself, or a JSON column for a value without a relational shape. Get-only members
    /// are not stored, except O13's stored computed members (a generated <c>__stored_</c> backing field).
    /// </summary>
    private void MapValue(PropertyInfo property, Func<string, object> ignore, Func<string, PropertyBuilder> column, Type declaring)
    {
        if (!property.CanWrite)
        {
            if (declaring.GetField("__stored_" + property.Name, BindingFlags.NonPublic | BindingFlags.Instance) is null)
            {
                ignore(property.Name);
                return;
            }
            column(property.Name).HasField("__stored_" + property.Name).UsePropertyAccessMode(PropertyAccessMode.PreferFieldDuringConstruction);
            return;
        }
        if (!IsMapped(property))
        {
            ignore(property.Name);
            return;
        }
        if (SparkIdValues.TargetOf(property.PropertyType) is not null)
        {
            if (SparkIdConverters.ForUnstored(property.PropertyType) is { } converter)
                column(property.Name).HasConversion(converter);
            return;
        }
        if (NeedsJson(property.PropertyType))
        {
            var json = column(property.Name);
            json.HasConversion(SparkJsonColumns.Converter(property.PropertyType), SparkJsonColumns.Comparer(property.PropertyType));
            Setup.Dialect.JsonColumn(json);
        }
    }

    /// <summary>Whether EF can't map the type as a column (or a primitive collection) itself.</summary>
    private static bool NeedsJson(Type type)
    {
        var clr = Nullable.GetUnderlyingType(type) ?? type;
        if (clr.IsPrimitive || clr.IsEnum || clr == typeof(string) || clr == typeof(decimal) || clr == typeof(DateTime)
            || clr == typeof(DateTimeOffset) || clr == typeof(DateOnly) || clr == typeof(TimeOnly) || clr == typeof(TimeSpan)
            || clr == typeof(Guid) || clr == typeof(byte[]))
            return false;
        if (SparkRelationalModel.CollectionElement(clr) is { } element && clr.IsGenericType
            && clr.GetGenericTypeDefinition() == typeof(List<>) && !NeedsJson(element) && !SparkIdValues.IsId(element))
            return false; // a primitive collection: EF's own JSON column, queryable
        return true;
    }

    /// <summary>A member the document stores: not marked <c>[JsonIgnore]</c> (System.Text.Json's or Newtonsoft's).</summary>
    private static bool IsMapped(PropertyInfo property)
        => !property.GetCustomAttributes(true).Any(a => a.GetType().Name == "JsonIgnoreAttribute");

    private void MapChildTable(EntityTypeBuilder entity, SparkTable owner, PropertyInfo property, Type element)
    {
        var rowKey = element.GetProperties().FirstOrDefault(p => p.GetCustomAttribute<ValueKeyAttribute>() is not null)?.Name
            ?? element.GetProperty("Id")?.Name
            ?? throw new InvalidOperationException($"{element.FullName} has no row key: [ValueObject] types get a generated Id, or mark one with [ValueKey].");
        entity.OwnsMany(element, property.Name, child =>
        {
            child.ToTable(owner.Name + property.Name);
            child.WithOwner().HasForeignKey(OwnerIdProperty);
            var ownerKey = child.Property(owner.IdProperty.PropertyType, OwnerIdProperty);
            KeyColumn(ownerKey, owner.IdProperty.PropertyType, owner);
            child.Property(rowKey).HasMaxLength(KeyLength).ValueGeneratedNever();
            child.HasKey(OwnerIdProperty, rowKey);
            foreach (var member in element.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                if (member.Name == rowKey)
                    continue;
                if (ReferenceTarget(member) is { } target && IsMapped(member) && member.CanWrite)
                {
                    var column = child.Property(member.Name);
                    if (Setup.Model.TableOf(target) is { } targetTable)
                        KeyColumn(column, member.PropertyType, targetTable);
                    else if (SparkIdConverters.ForUnstored(member.PropertyType) is { } converter)
                        column.HasConversion(converter);
                    continue;
                }
                MapValue(member, child.Ignore, name => child.Property(name), element);
            }
        });
    }

    private void MapJoin(ModelBuilder modelBuilder, SparkJoinTable join)
    {
        var owner = Setup.Model.RequireTable(join.Owner);
        modelBuilder.SharedTypeEntity<Dictionary<string, object>>(join.Name, b =>
        {
            KeyColumn(b.IndexerProperty(owner.IdProperty.PropertyType, OwnerIdProperty), owner.IdProperty.PropertyType, owner);
            var target = b.IndexerProperty(join.ElementType, TargetIdProperty);
            if (join.Target is { } targetTable)
                KeyColumn(target, join.ElementType, targetTable);
            else
            {
                if (SparkIdConverters.ForUnstored(join.ElementType) is { } converter)
                    target.HasConversion(converter);
                target.HasMaxLength(KeyLength).UseCollation(Setup.Dialect.KeyCollation);
            }
            b.HasKey(OwnerIdProperty, TargetIdProperty);
            b.HasOne(owner.Root).WithMany().HasForeignKey(OwnerIdProperty).OnDelete(DeleteBehavior.Cascade);
            if (join.Target is { } t && join.ElementType == t.IdProperty.PropertyType)
                b.HasOne(t.Root).WithMany().HasForeignKey(TargetIdProperty).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(TargetIdProperty);
        });
    }
}

/// <summary>One EF model per Spark model: two stores (two test hosts) with different stored types get two models.</summary>
internal sealed class SparkModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (context.GetType(), ((SparkDbContext)context).Setup, designTime);
}
