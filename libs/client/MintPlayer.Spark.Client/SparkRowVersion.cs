using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// A row and the version of it the caller saw (#467, D14): what a delete names, so a row changed
/// since is refused with a 409 rather than lost.
/// </summary>
/// <param name="Id">The object's id.</param>
/// <param name="Etag">Its change vector — a query result row's <c>Etag</c>, or a loaded object's.</param>
public sealed record SparkRowVersion(string Id, string Etag)
{
    /// <summary>A query result row, as it was listed.</summary>
    public static SparkRowVersion Of(QueryResultItem row)
        => new(row.Id, row.Etag ?? throw new ArgumentException($"Row '{row.Id}' has no etag: it has no document behind it.", nameof(row)));

    /// <summary>A loaded object, as it was loaded.</summary>
    public static SparkRowVersion Of(PersistentObject obj)
        => new(obj.Id ?? throw new ArgumentException("The object has no id.", nameof(obj)),
            obj.Etag ?? throw new ArgumentException($"Object '{obj.Id}' has no etag.", nameof(obj)));
}
