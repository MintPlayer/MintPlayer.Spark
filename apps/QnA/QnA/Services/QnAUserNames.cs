using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.History;
using Raven.Client.Documents;

namespace QnA.Services;

/// <summary>
/// Names in History's revision lists (#460 D8): audit fields store user ids, and a name is looked up
/// only when the list is read. A deleted account is missing from the answer, so it shows as a deleted
/// user — nothing had to be rewritten when it was deleted.
/// </summary>
public sealed partial class QnAUserNames : IHistoryUserNameResolver
{
    [Inject] private readonly IDocumentStore documentStore;

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return new Dictionary<string, string>();

        using var session = documentStore.OpenAsyncSession();
        var users = await session.LoadAsync<SparkUser>(userIds.Distinct(StringComparer.Ordinal), cancellationToken);
        return users
            .Where(u => u.Value is { UserName.Length: > 0 })
            .ToDictionary(u => u.Key, u => u.Value!.UserName!, StringComparer.Ordinal);
    }
}
