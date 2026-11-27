using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace MintPlayer.Spark.Relational.Schema;

/// <summary>
/// Spark's migration differ (#536 D24, D16), in place of EF's: the operations synchronize scaffolds and the test schemas
/// apply come from here.
/// <list type="bullet">
/// <item>D16: a column added to rows that already exist holds the CLR default those rows read as. EF gives a required
/// value column its type's default, but a required collection column none, which PostgreSQL refuses for the
/// existing rows ("contains null values"). It gets the empty collection: <c>'[]'</c> in JSON, <c>'{}'</c> for a native
/// array.</item>
/// <item>O19: a table's columns are paired by their model attribute before EF's name and shape rules, so a renamed
/// attribute keeps its column's data.</item>
/// <item>D32: EF ignores an entity type mapped to a view. A Spark view carries its SQL and the tables it reads as
/// annotations; a view is dropped first and created last when it appears, disappears, its SQL changes, or an
/// operation touches a table it reads (PostgreSQL refuses to alter a column a view depends on, and SQL Server keeps a
/// view's stale column list).</item>
/// <item>D6: on PostgreSQL a table gets its change-stamp trigger once its stamp column exists.</item>
/// </list>
/// </summary>
#pragma warning disable EF1001 // MigrationsModelDiffer is pubternal; a test constructs this class, so a constructor change in an EF minor fails the build.
public sealed class SparkMigrationsModelDiffer : Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer
{
    /// <summary>A view's SELECT, on its row's entity type.</summary>
    public const string ViewSqlAnnotation = "Spark:ViewSql";

    /// <summary>The tables a view reads, comma-separated, on its row's entity type.</summary>
    public const string ViewTablesAnnotation = "Spark:ViewTables";

    private readonly ISqlGenerationHelper sql;
    private readonly ICurrentDbContext currentContext;

    public SparkMigrationsModelDiffer(
        IRelationalTypeMappingSource typeMappingSource,
        IMigrationsAnnotationProvider migrationsAnnotationProvider,
        IRelationalAnnotationProvider relationalAnnotationProvider,
        IRowIdentityMapFactory rowIdentityMapFactory,
        CommandBatchPreparerDependencies commandBatchPreparerDependencies,
        ISqlGenerationHelper sql,
        ICurrentDbContext currentContext)
        : base(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider, rowIdentityMapFactory, commandBatchPreparerDependencies)
    {
        this.sql = sql;
        this.currentContext = currentContext;
    }

    /// <summary>EF's own answer looks at tables only; a view-only change is a difference too.</summary>
    public override bool HasDifferences(IRelationalModel? source, IRelationalModel? target) => GetDifferences(source, target).Count > 0;

    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = base.GetDifferences(source, target);
        if (source is not null && target is not null)
            AddCollectionDefaults(operations, target);
        return WithViews(WithStampTriggers(operations), source, target);
    }

    /// <summary>
    /// O19: a table's columns are paired by their model attribute (<see cref="Model.SparkDbContext.AttributeIdAnnotation"/>)
    /// first. EF pairs by name and then guesses by shape, so on its own:
    /// <list type="bullet">
    /// <item>an attribute renamed with a new type became a drop and an add, losing the column's data. Paired, EF's column
    /// diff renames the column, then alters its type. The database converts the data where it can (int to bigint, a
    /// number to text); where it can't, the migration fails, and never silently empties the column.</item>
    /// <item>an attribute removed while a new one of the same shape was added became a rename, carrying the old
    /// attribute's data into the new one.</item>
    /// </list>
    /// EF's own rules follow, each allowed only where one side has no attribute: a column the model doesn't describe
    /// (the key, the stamp, a snapshot from before attribute ids) is paired as EF would. A choice made here can't be
    /// undone after the fact: <c>DiffCollection</c> records every pair for the index and key diffs, which is why the
    /// rules are replaced, not the operations corrected.
    /// </summary>
    protected override IEnumerable<MigrationOperation> Diff(IEnumerable<IColumn> source, IEnumerable<IColumn> target, DiffContext diffContext)
        => DiffCollection(
            source,
            target,
            diffContext,
            Diff,
            (t, c) => Add(t, c),
            Remove,
            (s, t, _) => AttributeIdOf(s) is { } id && id == AttributeIdOf(t),
            (s, t, _) => EitherUnattributed(s, t) && string.Equals(s.Name, t.Name, StringComparison.OrdinalIgnoreCase),
            (s, t, c) => EitherUnattributed(s, t) && SamePropertyOnSamePath(s, t, c),
            (s, t, _) => EitherUnattributed(s, t) && s.PropertyMappings.Any(sm =>
                t.PropertyMappings.Any(tm => string.Equals(sm.Property.Name, tm.Property.Name, StringComparison.OrdinalIgnoreCase))),
            (s, t, c) => EitherUnattributed(s, t) && ColumnStructureEquals(s, t) && SamePropertyOnSamePath(s, t, c),
            (s, t, _) => EitherUnattributed(s, t) && ColumnStructureEquals(s, t) && ColumnAnnotationsEqual(s, t, matchValues: true),
            (s, t, _) => EitherUnattributed(s, t) && ColumnStructureEquals(s, t) && ColumnAnnotationsEqual(s, t, matchValues: false),
            (s, t, _) => EitherUnattributed(s, t) && ColumnStructureEquals(s, t));

    /// <summary>The model attribute a column stores, if the model describes it.</summary>
    internal static string? AttributeIdOf(IColumn column)
        => column.PropertyMappings
            .Select(m => m.Property.FindAnnotation(Model.SparkDbContext.AttributeIdAnnotation)?.Value as string)
            .FirstOrDefault(id => id is not null);

    /// <summary>Two attributed columns are one column only by their id: EF's name and shape rules don't apply to them.</summary>
    private static bool EitherUnattributed(IColumn source, IColumn target) => AttributeIdOf(source) is null || AttributeIdOf(target) is null;

    // EF's column-pairing rules (MigrationsModelDiffer.Diff over columns and its private helpers), re-expressed with
    // public API.

    private static bool SamePropertyOnSamePath(IColumn source, IColumn target, DiffContext diffContext)
        => source.PropertyMappings.Any(sm => target.PropertyMappings.Any(tm =>
            string.Equals(sm.Property.Name, tm.Property.Name, StringComparison.OrdinalIgnoreCase)
            && EntityTypePathEquals(sm.Property.DeclaringType, tm.Property.DeclaringType, diffContext)));

    private static bool ColumnAnnotationsEqual(IColumn source, IColumn target, bool matchValues)
    {
        var sourceAnnotations = source.GetAnnotations().ToList();
        var targetAnnotations = target.GetAnnotations().ToList();
        if (sourceAnnotations.Count != targetAnnotations.Count)
            return false;
        foreach (var sourceAnnotation in sourceAnnotations)
        {
            var match = targetAnnotations.FindIndex(t => t.Name == sourceAnnotation.Name && (!matchValues || t.Value == sourceAnnotation.Value));
            if (match < 0)
                return false;
            targetAnnotations.RemoveAt(match);
        }
        return true;
    }

    private static bool ColumnStructureEquals(IColumn source, IColumn target)
    {
        if (!source.TryGetDefaultValue(out var sourceDefault))
            sourceDefault = null;
        if (!target.TryGetDefaultValue(out var targetDefault))
            targetDefault = null;
        return source.StoreType == target.StoreType
            && source.IsRowVersion == target.IsRowVersion
            && source.IsNullable == target.IsNullable
            && source.Precision == target.Precision
            && source.Scale == target.Scale
            && source.IsUnicode == target.IsUnicode
            && source.MaxLength == target.MaxLength
            && source.IsFixedLength == target.IsFixedLength
            && source.Collation == target.Collation
            && MultilineEquals(source.Comment, target.Comment)
            && source.IsStored == target.IsStored
            && MultilineEquals(source.ComputedColumnSql, target.ComputedColumnSql)
            && Equals(sourceDefault, targetDefault)
            && MultilineEquals(source.DefaultValueSql, target.DefaultValueSql);
    }

    private static bool EntityTypePathEquals(ITypeBase source, ITypeBase target, DiffContext diffContext)
    {
        var sourceTable = diffContext.FindTable(source);
        var targetTable = diffContext.FindTable(target);
        if ((sourceTable is null && targetTable is null)
            || (sourceTable?.EntityTypeMappings.Count() == 1 && targetTable?.EntityTypeMappings.Count() == 1))
            return true;
        if (source.Name != target.Name)
            return false;
        if (source is IEntityType sourceEntity && target is IEntityType targetEntity)
        {
            var nextSource = sourceTable?.GetRowInternalForeignKeys(sourceEntity).FirstOrDefault()?.PrincipalEntityType;
            var nextTarget = targetTable?.GetRowInternalForeignKeys(targetEntity).FirstOrDefault()?.PrincipalEntityType;
            return (nextSource is null && nextTarget is null)
                || (nextSource is not null && nextTarget is not null && EntityTypePathEquals(nextSource, nextTarget, diffContext));
        }
        if (source is IComplexType sourceComplex && target is IComplexType targetComplex)
            return EntityTypePathEquals(sourceComplex.ComplexProperty.DeclaringType, targetComplex.ComplexProperty.DeclaringType, diffContext);
        return false;
    }

    private static bool MultilineEquals(string? source, string? target)
        => ReferenceEquals(source, target)
           || (source is not null && target is not null && string.Equals(source.ReplaceLineEndings(), target.ReplaceLineEndings(), StringComparison.Ordinal));

    private SparkRelationalDialect Dialect => currentContext.Context is Model.SparkDbContext spark
        ? spark.Setup.Dialect
        : SparkRelationalDialect.ForProvider(currentContext.Context.Database.ProviderName);

    /// <summary>
    /// D6: where the stamp's column type does not move itself on update (PostgreSQL), the table gets the dialect's
    /// trigger once its stamp column exists: after the table is created, or the column added.
    /// </summary>
    private IReadOnlyList<MigrationOperation> WithStampTriggers(IReadOnlyList<MigrationOperation> operations)
    {
        var stamped = operations.OfType<CreateTableOperation>()
            .Where(c => c.Columns.Any(col => col.Name == Model.SparkDbContext.StampProperty)).Select(c => (c.Name, c.Schema))
            .Concat(operations.OfType<AddColumnOperation>().Where(a => a.Name == Model.SparkDbContext.StampProperty).Select(a => (Name: a.Table, a.Schema)))
            .ToList();
        if (stamped.Count == 0)
            return operations;
        var dialect = Dialect;
        var triggers = stamped
            .Select(t => dialect.StampTriggerSql(sql.DelimitIdentifier(t.Name, t.Schema)))
            .OfType<string>()
            .Select(trigger => new SqlOperation { Sql = trigger })
            .ToList();
        return triggers.Count == 0 ? operations : [.. operations, .. triggers];
    }

    private IReadOnlyList<MigrationOperation> WithViews(IReadOnlyList<MigrationOperation> operations, IRelationalModel? source, IRelationalModel? target)
    {
        var before = Views(source);
        var after = Views(target);
        if (before.Count == 0 && after.Count == 0)
            return operations;

        var touched = operations.OfType<ITableMigrationOperation>().Select(o => o.Table)
            .Concat(operations.OfType<RenameTableOperation>().SelectMany(o => new[] { o.Name, o.NewName ?? o.Name }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Changed(SparkViewDefinition view, Dictionary<(string?, string), SparkViewDefinition> other)
            => !other.TryGetValue(view.Key, out var o) || o.Sql != view.Sql
               || view.Tables.Any(touched.Contains) || o.Tables.Any(touched.Contains);

        var drops = before.Values.Where(v => Changed(v, after)).OrderBy(v => v.Name, StringComparer.Ordinal)
            .Select(v => new SqlOperation { Sql = $"DROP VIEW {sql.DelimitIdentifier(v.Name, v.Schema)};" });
        var dialect = Dialect;
        var creates = after.Values.Where(v => Changed(v, before)).OrderBy(v => v.Name, StringComparer.Ordinal)
            .Select(v => new SqlOperation { Sql = dialect.CreateViewSql(sql.DelimitIdentifier(v.Name, v.Schema), v.Sql) });
        return [.. drops, .. operations, .. creates];
    }

    private sealed record SparkViewDefinition(string Name, string? Schema, string Sql, string[] Tables)
    {
        public (string?, string) Key => (Schema, Name);
    }

    private static Dictionary<(string?, string), SparkViewDefinition> Views(IRelationalModel? model)
        => model?.Model.GetEntityTypes()
            .Where(e => e.FindAnnotation(ViewSqlAnnotation)?.Value is string && e.GetViewName() is not null)
            .Select(e => new SparkViewDefinition(e.GetViewName()!, e.GetViewSchema(), (string)e[ViewSqlAnnotation]!,
                ((string?)e[ViewTablesAnnotation] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .ToDictionary(v => v.Key)
           ?? [];

    private static void AddCollectionDefaults(IReadOnlyList<MigrationOperation> operations, IRelationalModel target)
    {
        foreach (var add in operations.OfType<AddColumnOperation>())
        {
            if (add.IsNullable || add.DefaultValue is not null || add.DefaultValueSql is not null || add.ComputedColumnSql is not null)
                continue;
            var column = target.FindTable(add.Table, add.Schema)?.FindColumn(add.Name);
            // SQL, not a value: a value goes through the column's collection converter. Npgsql stores a primitive
            // collection as a native array (text[]), whose empty literal is '{}'; elsewhere it is JSON.
            if (column?.PropertyMappings.FirstOrDefault()?.Property is { } property && IsCollection(property))
                add.DefaultValueSql = column.StoreType.EndsWith("[]", StringComparison.Ordinal) ? "'{}'" : "'[]'";
        }
    }

    private static bool IsCollection(IProperty property)
        => property.IsPrimitiveCollection
           || (property.ClrType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.ClrType)
               && property.GetTypeMapping().Converter?.ProviderClrType == typeof(string));
}
#pragma warning restore EF1001
