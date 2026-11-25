using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using SqlDemo.Entities;

namespace SqlDemo.Actions;

/// <summary>
/// security.json lets anonymous visitors read Book, and startup refuses an anonymous grant whose row rule is undecided.
/// The catalogue is public on purpose.
/// </summary>
public partial class BookActions : DefaultPersistentObjectActions<Book>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "A public book catalogue: every book is meant to be listed to every visitor, and a book has no owner.";
}
