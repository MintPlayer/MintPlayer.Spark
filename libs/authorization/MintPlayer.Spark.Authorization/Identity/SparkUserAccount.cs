using MintPlayer.Spark.Abstractions.Authentication;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// A <see cref="SparkUser"/> as an <see cref="ISparkAccount"/>, for the account lifecycle hooks and the
/// <see cref="ISparkAccountDirectory"/> (<c>docs/notification_bell_PRD.md</c> G4). A view, not a copy: it reads
/// the user it wraps. <see cref="SparkUser"/> itself implements nothing, because its package
/// (<c>MintPlayer.Spark.Authorization.Abstractions</c>) depends on nothing web, while the contracts live in
/// <c>MintPlayer.Spark.Abstractions</c>.
/// </summary>
public sealed class SparkUserAccount(SparkUser user) : ISparkAccount
{
    /// <summary>The wrapped user: a hook of the application's own can cast it to its user type.</summary>
    public SparkUser User { get; } = user;

    public string Id => User.Id ?? throw new InvalidOperationException("A user without an id is not an account yet.");
    public string? UserName => User.UserName;
    public string? Email => User.Email;
    public bool EmailConfirmed => User.EmailConfirmed;
    public string? PreferredCulture => User.PreferredCulture;
}

/// <summary>The <see cref="ISparkAccountDirectory"/> over the stored <typeparamref name="TUser"/> documents.</summary>
internal sealed class SparkUserAccountDirectory<TUser>(IDocumentStore store) : ISparkAccountDirectory
    where TUser : SparkUser
{
    public async Task<IReadOnlyDictionary<string, ISparkAccount>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        var wanted = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
            return new Dictionary<string, ISparkAccount>();

        using var session = store.OpenAsyncSession();
        var users = await session.LoadAsync<TUser>(wanted, cancellationToken);
        return users
            .Where(pair => pair.Value?.Id is not null)
            .ToDictionary(pair => pair.Key, pair => (ISparkAccount)new SparkUserAccount(pair.Value!), StringComparer.OrdinalIgnoreCase);
    }
}
