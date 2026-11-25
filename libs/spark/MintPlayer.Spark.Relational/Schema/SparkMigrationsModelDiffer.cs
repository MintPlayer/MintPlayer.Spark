using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// The view handling of D32 (drop first, create last) joins it in M4d.
/// </summary>
#pragma warning disable EF1001 // MigrationsModelDiffer is pubternal; a test constructs this class, so a constructor change in an EF minor fails the build.
public sealed class SparkMigrationsModelDiffer : Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer
{
    public SparkMigrationsModelDiffer(
        IRelationalTypeMappingSource typeMappingSource,
        IMigrationsAnnotationProvider migrationsAnnotationProvider,
        IRelationalAnnotationProvider relationalAnnotationProvider,
        IRowIdentityMapFactory rowIdentityMapFactory,
        CommandBatchPreparerDependencies commandBatchPreparerDependencies)
        : base(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider, rowIdentityMapFactory, commandBatchPreparerDependencies)
    {
    }

    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = base.GetDifferences(source, target);
        if (source is null || target is null)
            return operations;

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
        return operations;
    }

    private static bool IsCollection(IProperty property)
        => property.IsPrimitiveCollection
           || (property.ClrType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.ClrType)
               && property.GetTypeMapping().Converter?.ProviderClrType == typeof(string));
}
#pragma warning restore EF1001
