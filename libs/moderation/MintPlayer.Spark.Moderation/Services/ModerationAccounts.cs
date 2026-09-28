using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>What Moderation needs to know about an account (fraud measures 1, 3, 6, 8, 10).</summary>
public sealed class ModerationAccount
{
    public required string Id { get; init; }
    /// <summary>When the account was created (<c>SparkUser.CreatedAtUtc</c>, backfilled in #460 M5). Null: unknown — treated as brand new.</summary>
    public DateTime? CreatedAtUtc { get; init; }
    /// <summary><c>SparkUser.RegistrationMethod</c> (password, external provider …), shown on the review surface.</summary>
    public string? RegistrationMethod { get; init; }
    /// <summary>The domain of the account's email, lower-cased; null without one.</summary>
    public string? EmailDomain { get; init; }

    /// <summary>Age in whole days at <paramref name="nowUtc"/>; 0 when unknown (fail closed).</summary>
    public int AgeDays(DateTime nowUtc) => CreatedAtUtc is { } created ? Math.Max(0, (int)Math.Floor((nowUtc - created).TotalDays)) : 0;
}

/// <summary>
/// The account side of moderation: reading accounts, and the Identity half of a suspension. The
/// default implementation reads <see cref="SparkUser"/> documents; replace it for another user store.
/// </summary>
public interface IModerationAccounts
{
    /// <summary>The accounts behind <paramref name="userIds"/>; ids without an account are absent from the result.</summary>
    Task<IReadOnlyDictionary<string, ModerationAccount>> GetAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);

    /// <summary>Locks the account out until <paramref name="untilUtc"/> (null: indefinitely) and refreshes its security stamp.</summary>
    Task LockOutAsync(string userId, DateTimeOffset? untilUtc, CancellationToken cancellationToken = default);

    /// <summary>Lifts the lockout and refreshes the security stamp.</summary>
    Task LiftLockOutAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IModerationAccounts"/> over ASP.NET Core Identity with Spark's RavenDB user store.
/// </summary>
/// <remarks>
/// Reads go straight to the user documents (one batched load, no Identity round trip). The lockout
/// goes through <see cref="UserManager{TUser}"/>, resolved lazily so a host without Identity can
/// still read accounts; <c>UpdateSecurityStampAsync</c> is what makes other sessions' cookies fail
/// their next validation (spike S-MOD-C measures how soon).
/// </remarks>
internal sealed partial class IdentityModerationAccounts<TUser> : IModerationAccounts
    where TUser : SparkUser
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IServiceProvider services;

    public async Task<IReadOnlyDictionary<string, ModerationAccount>> GetAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, ModerationAccount>();

        using var session = documentStore.OpenAsyncSession();
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        var users = await session.LoadAsync<TUser>(ids, cancellationToken);
        return users.Where(u => u.Value is not null).ToDictionary(
            u => u.Key,
            u => new ModerationAccount
            {
                Id = u.Key,
                CreatedAtUtc = u.Value!.CreatedAtUtc is { } created ? DateTime.SpecifyKind(created, DateTimeKind.Utc) : null,
                RegistrationMethod = u.Value.RegistrationMethod,
                EmailDomain = DomainOf(u.Value.Email),
            },
            StringComparer.Ordinal);
    }

    public async Task LockOutAsync(string userId, DateTimeOffset? untilUtc, CancellationToken cancellationToken = default)
    {
        var users = services.GetService<UserManager<TUser>>();
        if (users is null)
            return;
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return;
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, untilUtc ?? DateTimeOffset.MaxValue);
        await users.UpdateSecurityStampAsync(user);
    }

    public async Task LiftLockOutAsync(string userId, CancellationToken cancellationToken = default)
    {
        var users = services.GetService<UserManager<TUser>>();
        if (users is null)
            return;
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return;
        await users.SetLockoutEndDateAsync(user, null);
        await users.UpdateSecurityStampAsync(user);
    }

    internal static string? DomainOf(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var at = email.LastIndexOf('@');
        return at < 0 || at == email.Length - 1 ? null : email[(at + 1)..].Trim().ToLowerInvariant();
    }
}
