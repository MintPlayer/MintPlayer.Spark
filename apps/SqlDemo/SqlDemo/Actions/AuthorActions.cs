using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using SqlDemo.Entities;

namespace SqlDemo.Actions;

/// <summary>
/// security.json lets anonymous visitors read Author, and startup refuses an anonymous grant whose row rule is undecided.
/// The authors are part of the public catalogue, as in <see cref="BookActions"/>.
/// </summary>
public partial class AuthorActions : DefaultPersistentObjectActions<Author>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "A public book catalogue: every author is listed beside their books to every visitor, and an author has no owner.";
}
