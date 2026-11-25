using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MintPlayer.Spark.Relational.Schema;

/// <summary>Migration operations as one line each, for synchronize's report and the startup check's error (D33).</summary>
public static class SparkMigrationOperations
{
    /// <summary>The operation's kind and what it acts on, e.g. <c>AddColumn Books.Isbn</c>.</summary>
    public static string Describe(MigrationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
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
}
