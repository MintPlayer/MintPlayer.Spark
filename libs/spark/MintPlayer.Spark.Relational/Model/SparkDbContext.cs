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
    internal SparkContextSetup(
        SparkRelationalModel model, SparkRelationalDialect dialect, IReadOnlyList<Action<ModelBuilder>> configureModel,
        Func<Type, string, Guid?>? attributeId = null)
    {
        Model = model;
        Dialect = dialect;
        ConfigureModel = configureModel;
        AttributeId = attributeId ?? ((_, _) => null);
    }

    /// <summary>The app model's attribute id of a stored property (<see cref="Abstractions.Storage.ISparkStoredTypes.AttributeId"/>).</summary>
    public Func<Type, string, Guid?> AttributeId { get; }

    public SparkRelationalModel Model { get; }

    public SparkRelationalDialect Dialect { get; }

    /// <summary>The app's <c>ConfigureModel</c> hooks (O9), run last.</summary>
    public IReadOnlyList<Action<ModelBuilder>> ConfigureModel { get; }

    /// <summary>
    /// The SQL EF rendered for each view (D32), by view name; <see langword="null"/> until the store has rendered them.
    /// The model built before that is the render model, whose views carry no SQL; it is a different cached EF model.
    /// </summary>
    public IReadOnlyDictionary<string, SparkViewSql>? RenderedViews { get; internal set; }

    /// <summary>
    /// Which tables keep revisions (#536 S8), by table name, as <see cref="Abstractions.Storage.ISparkRevisions.ConfigureAsync"/>
    /// set them. Held here so every write path, in any context of the store, finds them.
    /// </summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, Abstractions.Storage.SparkRevisionSettings> Revisions { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings of <paramref name="table"/> when it keeps revisions; otherwise <see langword="null"/>.</summary>
    internal Abstractions.Storage.SparkRevisionSettings? RevisionsOf(string table)
        => Revisions.TryGetValue(table, out var settings) && settings.Enabled ? settings : null;
}

/// <summary>A view's SQL, and the tables it reads (which the differ watches).</summary>
public sealed record SparkViewSql(string Sql, IReadOnlyList<string> Tables);

/// <summary>
/// The <see cref="DbContext"/> Spark builds from its model (#536 D1): no <c>DbSet</c> properties and no EF attributes on
/// the entities, so a feature library ships its entities with no EF code.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>One table per collection, TPH for stored derived types; the key column per <see cref="SparkKeyShape"/>.</item>
/// <item>D3: a <c>Version</c> bigint concurrency token on every table, moved by the session.</item>
/// <item>D6: a <c>SparkChangeStamp</c> on every table, moved by the database, which the change feed reads.</item>
/// <item>D7: text keys and the FK columns pointing at them compare bytes (the dialect's binary collation).</item>
/// <item>D25: a <c>[ValueObject]</c> collection is a child table keyed by (owner id, row key), deleted with its owner;
/// <see cref="PositionProperty"/> keeps the list's order.</item>
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

    /// <summary>D6: the change stamp, which the database moves on every insert and update of the row (#536 M4e).</summary>
    public const string StampProperty = "SparkChangeStamp";

    /// <summary>
    /// D25/D26: a child or join row's position in its list, so a list loads in the order it was stored, as a document
    /// keeps it (and <c>[Sortable]</c>, "the order is the array position", persists).
    /// </summary>
    public const string PositionProperty = "SparkPosition";

    internal const int KeyLength = SparkFrameworkSchema.KeyLength;

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
            Setup.Dialect.StampColumn(entity.Property<ulong>(StampProperty));
            entity.HasIndex(StampProperty);

            foreach (var derived in table.Derived)
                modelBuilder.Entity(derived).HasBaseType(BaseOf(derived, table));
            foreach (var type in table.Derived.Prepend(table.Root))
                MapMembers(modelBuilder.Entity(type), type, table);
        }

        foreach (var join in model.Joins)
            MapJoin(modelBuilder, join);

        foreach (var view in model.Views)
            MapView(modelBuilder, view);

        // The framework schema's tables, which its own migrations create (D24): used here, never migrated here.
        SparkFrameworkSchema.AddTables(modelBuilder, Setup.Dialect, excludeFromMigrations: true);

        foreach (var configure in Setup.ConfigureModel)
            configure(modelBuilder);
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
        => optionsBuilder
            .AddInterceptors(Storage.SparkListPositions.Interceptor)
            .ReplaceService<IModelCacheKeyFactory, SparkModelCacheKeyFactory>()
            .ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsModelDiffer, Schema.SparkMigrationsModelDiffer>();

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

            // A get-only id (O13) is a plain value: stored through its field when it has one, without a foreign key.
            if (ReferenceTarget(property) is { } target && IsMapped(property) && property.CanWrite)
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

            MapValue(property, entity.Ignore, name => entity.Property(name), name => entity.PrimitiveCollection(name));
        }

        // D24: the column remembers its attribute, so a rename (same attribute, new name) can't pass as a drop and an add.
        foreach (var property in SparkRelationalModel.DeclaredStoredProperties(type))
            if (entity.Metadata.FindDeclaredProperty(property.Name) is { } column && Setup.AttributeId(type, property.Name) is { } attributeId)
                column.SetAnnotation(AttributeIdAnnotation, attributeId.ToString("D"));
    }

    /// <summary>The app model's attribute id, on the column's property (D24).</summary>
    public const string AttributeIdAnnotation = "Spark:AttributeId";

    /// <summary>The type an id property points at: <see cref="Id{T}"/>'s <c>T</c>, or a <c>[Reference]</c> string's target.</summary>
    private static Type? ReferenceTarget(PropertyInfo property)
        => SparkIdValues.TargetOf(property.PropertyType)
           ?? (property.PropertyType == typeof(string) ? property.GetCustomAttribute<ReferenceAttribute>()?.TargetType : null);

    /// <summary>
    /// A plain member: a column EF maps itself, or a JSON column for a value without a relational shape. A get-only
    /// member is stored when it has O13's generated backing field (<see cref="SparkStoredGetterAttribute.FieldPrefix"/>):
    /// it takes the same column as a settable member of its type, and EF writes the field when it loads a row and reads
    /// the getter when it saves, so the column holds the getter's value as the document does. Without the field it is
    /// not stored.
    /// </summary>
    private void MapValue(PropertyInfo property, Func<string, object> ignore, Func<string, PropertyBuilder> column,
        Func<string, PrimitiveCollectionBuilder> list)
    {
        var storedField = property.CanWrite ? null : StoredField(property);
        if (!IsMapped(property) || (!property.CanWrite && storedField is null))
        {
            ignore(property.Name);
            return;
        }

        PropertyBuilder Column() => storedField is null
            ? column(property.Name)
            : column(property.Name).HasField(storedField).UsePropertyAccessMode(PropertyAccessMode.PreferFieldDuringConstruction);

        if (SparkIdValues.TargetOf(property.PropertyType) is not null)
        {
            if (SparkIdConverters.ForUnstored(property.PropertyType) is { } converter)
                Column().HasConversion(converter);
            else if (storedField is not null)
                Column();
            return;
        }
        if (NeedsJson(property.PropertyType))
        {
            var json = Column();
            json.HasConversion(SparkJsonColumns.Converter(property.PropertyType), SparkJsonColumns.Comparer(property.PropertyType));
            Setup.Dialect.JsonColumn(json);
        }
        else if (property.PropertyType != typeof(byte[]) && SparkRelationalModel.CollectionElement(property.PropertyType) is { } element)
        {
            if (!IsNamedEnum(element) && storedField is null)
                return; // EF maps a settable primitive collection itself
            var primitives = list(property.Name);
            if (storedField is not null)
                primitives.HasField(storedField).UsePropertyAccessMode(PropertyAccessMode.PreferFieldDuringConstruction);
            if (IsNamedEnum(element))
                primitives.ElementType(e => e.HasConversion<string>()); // O21: a list of enums holds their names, as the document does
        }
        else if (property.PropertyType == typeof(string) || IsNamedEnum(property.PropertyType))
            PlainColumn(Column(), property.PropertyType);
        else if (storedField is not null)
            Column(); // EF maps a settable scalar itself; a get-only one only through its field
    }

    /// <summary>O13: the generated backing field of a get-only member, on the type that declares the member.</summary>
    private static string? StoredField(PropertyInfo property)
        => property.DeclaringType?.GetField(
            SparkStoredGetterAttribute.FieldPrefix + property.Name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.Name;

    /// <summary>
    /// A scalar column's shape where it differs from EF's default: text compares ignoring case (O20), and an enum is
    /// stored by its name (O21), both as on RavenDB.
    /// </summary>
    private void PlainColumn(PropertyBuilder column, Type type)
    {
        if (type == typeof(string))
            Setup.Dialect.TextColumn(column);
        else if (IsNamedEnum(type))
        {
            column.HasConversion<string>();
            Setup.Dialect.TextColumn(column);
        }
    }

    /// <summary>
    /// O21: an enum (or nullable enum) stored by its name, as RavenDB stores it, so sorting and a changed member order
    /// mean the same on every store. A <c>[Flags]</c> enum stays a number: its name is a list ("Read, Write") that a
    /// bitwise filter cannot use.
    /// </summary>
    internal static bool IsNamedEnum(Type type)
        => (Nullable.GetUnderlyingType(type) ?? type) is { IsEnum: true } clr && clr.GetCustomAttribute<FlagsAttribute>() is null;

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
            child.Property<int>(PositionProperty);
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
                MapValue(member, child.Ignore, name => child.Property(name), name => child.PrimitiveCollection(name));
            }
        });
    }

    /// <summary>
    /// D32: the view's row type is mapped to the SQL view of the view's name, which EF never creates itself. The row's
    /// <c>Id</c> holds the collection's key with the collection's converter, so a <see cref="string"/> row id reads as the
    /// whole id (<c>people/…</c>) as on the other providers. The rendered SQL and the tables it reads ride on the entity
    /// type as annotations, so the snapshot keeps them and <see cref="Schema.SparkMigrationsModelDiffer"/> compares them.
    /// </summary>
    private void MapView(ModelBuilder modelBuilder, Abstractions.Views.ISparkView view)
    {
        var collection = Setup.Model.RequireTable(view.CollectionType);
        var entity = modelBuilder.Entity(view.RowType);
        entity.ToView(view.Name);
        var id = view.RowType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        if (id is { CanWrite: true })
        {
            entity.HasKey(id.Name);
            KeyColumn(entity.Property(id.Name), id.PropertyType, collection);
        }
        else
            entity.HasNoKey();

        foreach (var property in view.RowType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            if (property.Name == id?.Name && id.CanWrite)
                continue;
            if (ReferenceTarget(property) is { } target && IsMapped(property) && property.CanWrite)
            {
                var column = entity.Property(property.Name);
                if (Setup.Model.TableOf(target) is { } targetTable)
                    KeyColumn(column, property.PropertyType, targetTable);
                else if (SparkIdConverters.ForUnstored(property.PropertyType) is { } converter)
                    column.HasConversion(converter);
                continue;
            }
            MapValue(property, entity.Ignore, name => entity.Property(name), name => entity.PrimitiveCollection(name));
        }

        if (Setup.RenderedViews?.GetValueOrDefault(view.Name) is { } rendered)
        {
            entity.HasAnnotation(Schema.SparkMigrationsModelDiffer.ViewSqlAnnotation, rendered.Sql);
            entity.HasAnnotation(Schema.SparkMigrationsModelDiffer.ViewTablesAnnotation, string.Join(",", rendered.Tables));
        }
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
            b.IndexerProperty<int>(PositionProperty);
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
        => (context.GetType(), ((SparkDbContext)context).Setup, ((SparkDbContext)context).Setup.RenderedViews is null, designTime);
}
