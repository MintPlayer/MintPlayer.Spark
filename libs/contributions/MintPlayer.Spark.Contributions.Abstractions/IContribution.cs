namespace MintPlayer.Spark.Contributions;

/// <summary>
/// One user's contribution for one slot of one target. Implemented by the generated
/// <c>{Target}{Property}Contribution</c> types; id
/// <c>{targetId}/{Name}Contributions/{slot…}/User/{userId}</c>.
/// </summary>
public interface IContribution
{
    /// <summary>The document id of the target entity.</summary>
    string TargetId { get; set; }

    /// <summary>The user who wrote this contribution. Never changed after creation.</summary>
    string ContributorId { get; set; }

    /// <summary>
    /// When the contribution was last written, in UTC. A <see cref="DateTime"/> rather than a
    /// <see cref="DateTimeOffset"/>, because index projections lose the offset.
    /// </summary>
    DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The contributor's display name, filled at read time by the generated contributions query
    /// (through <c>ISparkUserNameResolver</c>) and never stored (<c>[JsonIgnore]</c>); <see langword="null"/>
    /// when no resolver is registered or the user is gone.
    /// </summary>
    string? ContributorName { get; set; }
}

/// <summary>
/// The current (latest non-hidden) version of one slot of one target: a cache the library owns and can
/// rebuild (<see cref="IContributions.RebuildCurrentAsync"/>). Implemented by the generated
/// <c>{Target}{Property}Current</c> types; id <c>{targetId}/{Name}/{slot…}</c>.
/// </summary>
public interface ICurrentContribution
{
    /// <summary>The user whose contribution is current.</summary>
    string ContributorId { get; set; }

    /// <summary>The document id of the contribution this version was copied from.</summary>
    string ContributionId { get; set; }

    /// <summary>The <see cref="IContribution.UpdatedAt"/> of that contribution, in UTC.</summary>
    DateTime UpdatedAt { get; set; }
}
