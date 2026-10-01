using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// Maintenance operations on contributions. Saving, withdrawing and hiding contributions goes through
/// the normal persistent-object pipeline (the target's form and the generated types), not through this
/// service.
/// </summary>
public interface IContributions
{
    /// <summary>
    /// Recomputes every current document of <paramref name="targetId"/> from its contributions: for each
    /// slot the latest non-hidden contribution becomes current, and a slot with none left loses its
    /// current document. Idempotent; meant for repair and for a changed generated shape.
    /// </summary>
    /// <param name="targetId">The document id of the target entity, e.g. <c>Songs/1234</c>.</param>
    /// <param name="cancellationToken">Cancels the rebuild.</param>
    Task RebuildCurrentAsync(string targetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The app's domain rules for one contribution element type (PRD T7), e.g. script detection and line
/// counts for lyrics. Register one per element type in DI; the library calls it for every added or
/// edited row before the contribution is written, after its own slot checks.
/// </summary>
/// <typeparam name="TElement">The element type of a <see cref="ContributionAttribute"/> property.</typeparam>
public interface IContributionValidator<in TElement>
{
    /// <summary>
    /// Validates one row. Return no errors to accept it; any error refuses the whole save as a
    /// validation failure.
    /// </summary>
    /// <param name="targetId">The document id of the target entity.</param>
    /// <param name="element">The row as the user submitted it.</param>
    /// <param name="cancellationToken">Cancels the validation.</param>
    ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(string targetId, TElement element, CancellationToken cancellationToken = default);
}
