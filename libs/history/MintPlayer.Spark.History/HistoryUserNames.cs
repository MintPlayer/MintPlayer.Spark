using MintPlayer.Spark.Abstractions.Authentication;

namespace MintPlayer.Spark.History;

/// <summary>
/// The app's <see cref="IHistoryUserNameResolver"/> as core's <see cref="ISparkUserNameResolver"/>, so
/// every feature that shows names (contributions' <c>ContributorName</c>) uses the same lookup.
/// </summary>
internal sealed class HistoryUserNames(IHistoryUserNameResolver inner) : ISparkUserNameResolver
{
    public Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
        => inner.ResolveAsync(userIds, cancellationToken);
}
