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
/// </list>
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
