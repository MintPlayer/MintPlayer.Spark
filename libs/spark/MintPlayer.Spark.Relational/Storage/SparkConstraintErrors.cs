using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// D30 (S4): a database constraint error becomes Spark's validation error naming the attribute; a duplicate primary key
/// is a concurrency failure (D3). The names the dialect read from the error are matched against EF's model, never the
/// message's wording, which SQL Server localizes. The raw message never leaves: it can carry the value itself.
/// </summary>
internal static class SparkConstraintErrors
{
    public static Exception Translate(DbContext db, SparkRelationalDialect dialect, DbUpdateException ex)
    {
        if (ex.InnerException is not { } inner || dialect.Classify(inner) is not { } error)
            return ex;
        var names = error.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The failed entries' types first: a column name such as "Name" exists on several tables.
        var failed = ex.Entries.Select(e => e.Metadata).ToHashSet();
        IReadOnlyList<IEntityType> types = [.. failed, .. db.Model.GetEntityTypes().Where(t => !failed.Contains(t))];

        switch (error.Kind)
        {
            case SparkConstraintKind.Unique:
                if (types.SelectMany(t => t.GetKeys()).Any(k => k.IsPrimaryKey() && k.GetName() is { } name && names.Contains(name)))
                    return new SparkStoreConcurrencyException("A row with this id exists already.", ex);
                var index = types.SelectMany(t => t.GetIndexes()).FirstOrDefault(i => i.GetDatabaseName() is { } name && names.Contains(name));
                return Validation(index?.Properties[0].Name, "must be unique");
            case SparkConstraintKind.ForeignKey:
                var fk = types.SelectMany(t => t.GetForeignKeys()).FirstOrDefault(f => f.GetConstraintName() is { } name && names.Contains(name));
                return Validation(fk?.Properties[0].Name, "refers to a row that does not exist");
            case SparkConstraintKind.NotNull:
                var property = types.SelectMany(t => t.GetProperties()).FirstOrDefault(p => p.GetColumnName() is { } name && names.Contains(name));
                return Validation(property?.Name, "is required");
            default:
                // Neither database names the column reliably (PostgreSQL never does): find the value that is too long.
                var tooLong = ex.Entries.SelectMany(e => e.Properties)
                    .FirstOrDefault(p => p.Metadata.GetMaxLength() is int max && p.CurrentValue is string s && s.Length > max);
                return Validation(tooLong?.Metadata.Name, $"is longer than {tooLong?.Metadata.GetMaxLength()} characters");
        }
    }

    // SparkValidationException takes no inner exception: the database's text stays in the server log only.
    private static SparkValidationException Validation(string? attribute, string rule) => new($"{attribute ?? "A value"} {rule}.", attribute);
}
