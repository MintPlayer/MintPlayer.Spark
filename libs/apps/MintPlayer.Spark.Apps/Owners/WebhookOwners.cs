using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Apps;

/// <summary>An owner the signed-in user may manage webhooks for.</summary>
public sealed record WebhookOwnerDescription(string Kind, string Key, string DisplayName);

/// <summary>
/// One kind of webhook owner (D18): who may manage its webhooks. Users and organizations are built in; a site adds
/// its own kinds with <see cref="AppsBuilder.AddOwnerProvider{TProvider}"/>.
/// </summary>
public interface IWebhookOwnerProvider
{
    /// <summary>The <see cref="WebhookOwner.Kind"/> this provider answers for.</summary>
    string Kind { get; }

    /// <summary>Whether <paramref name="userId"/> may see and change the webhooks of owner <paramref name="key"/>.</summary>
    Task<bool> CanManageAsync(string key, string userId, CancellationToken cancellationToken);

    /// <summary>The owners of this kind that <paramref name="userId"/> manages, for the settings page.</summary>
    Task<IReadOnlyList<WebhookOwnerDescription>> ListManageableAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>A user manages their own webhooks.</summary>
[Register(typeof(IWebhookOwnerProvider), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed class UserWebhookOwnerProvider : IWebhookOwnerProvider
{
    public string Kind => WebhookOwner.UserKind;

    public Task<bool> CanManageAsync(string key, string userId, CancellationToken cancellationToken)
        => Task.FromResult(string.Equals(key, userId, StringComparison.Ordinal));

    public Task<IReadOnlyList<WebhookOwnerDescription>> ListManageableAsync(string userId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<WebhookOwnerDescription>>([new(Kind, userId, "Your account")]);
}

/// <summary>
/// An organization's admins and owners manage its webhooks. Without <c>AddOrganizations()</c> nobody does.
/// </summary>
[Register(typeof(IWebhookOwnerProvider), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class OrganizationWebhookOwnerProvider : IWebhookOwnerProvider
{
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly IDocumentStore store;

    public string Kind => WebhookOwner.OrganizationKind;

    private ISparkOrganizations? Organizations => services.GetService(typeof(ISparkOrganizations)) as ISparkOrganizations;

    public async Task<bool> CanManageAsync(string key, string userId, CancellationToken cancellationToken)
        => Organizations is { } organizations
           && await organizations.GetRoleAsync(key, userId, cancellationToken) is { } role
           && role >= OrganizationRole.Admin;

    public async Task<IReadOnlyList<WebhookOwnerDescription>> ListManageableAsync(string userId, CancellationToken cancellationToken)
    {
        if (Organizations is not { } organizations)
            return [];
        var ids = (await organizations.GetMembershipsAsync(userId, cancellationToken))
            .Where(m => m.Value >= OrganizationRole.Admin)
            .Select(m => m.Key)
            .ToList();
        if (ids.Count == 0)
            return [];

        using var session = store.OpenAsyncSession();
        var loaded = await session.LoadAsync<Organization>(ids, cancellationToken);
        return loaded.Values
            .Where(o => o?.Id is not null)
            .Select(o => new WebhookOwnerDescription(Kind, o!.Id!, o.Name))
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>The registered owner providers by kind.</summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class WebhookOwnerResolver
{
    [Inject] private readonly IEnumerable<IWebhookOwnerProvider> providers;

    public IWebhookOwnerProvider? Find(string kind) => providers.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.Ordinal));

    public async Task<bool> CanManageAsync(WebhookOwner owner, string userId, CancellationToken cancellationToken)
        => Find(owner.Kind) is { } provider && await provider.CanManageAsync(owner.Key, userId, cancellationToken);

    public async Task<IReadOnlyList<WebhookOwnerDescription>> ListManageableAsync(string userId, CancellationToken cancellationToken)
    {
        var all = new List<WebhookOwnerDescription>();
        foreach (var provider in providers.DistinctBy(p => p.Kind))
            all.AddRange(await provider.ListManageableAsync(userId, cancellationToken));
        return all;
    }
}
