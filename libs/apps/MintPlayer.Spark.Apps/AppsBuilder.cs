using System.Reflection;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Apps.Services;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Apps;

/// <summary>What <c>spark.AddApps(apps => …)</c> configures.</summary>
public sealed class AppsBuilder
{
    internal List<WebhookEventRegistration> Events { get; } = [];
    internal List<Type> OwnerProviders { get; } = [];
    internal List<(IWebhookSigner Signer, string? PublicKey)> Signers { get; } = [];
    internal List<Action<SparkAppsOptions>> Configure { get; } = [];

    /// <summary>Every <c>[WebhookEvent]</c> type in <typeparamref name="T"/>'s assembly.</summary>
    public AppsBuilder AddEventsFromAssemblyContaining<T>() => AddEventsFromAssembly(typeof(T).Assembly);

    public AppsBuilder AddEventsFromAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<WebhookEventAttribute>() is not null).OrderBy(t => t.FullName, StringComparer.Ordinal))
            Events.Add(WebhookEventRegistration.FromAttribute(type));
        return this;
    }

    /// <summary>One <c>[WebhookEvent]</c> type.</summary>
    public AppsBuilder AddEvent<TEvent>()
    {
        Events.Add(WebhookEventRegistration.FromAttribute(typeof(TEvent)));
        return this;
    }

    /// <summary>A type without the attribute (e.g. from a package you don't own), under <paramref name="name"/>.</summary>
    public AppsBuilder AddEvent<TEvent>(string name, string? permission = null, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Events.Add(new WebhookEventRegistration(name, typeof(TEvent), permission, description));
        return this;
    }

    internal List<EntityEventRegistration> EntityEventRegistrations { get; } = [];

    /// <summary>
    /// Lifecycle events for <typeparamref name="T"/> (D6, D31): <c>{name}.created</c>, <c>.updated</c> and <c>.deleted</c>
    /// when it is written through Spark (a persistent object save or delete), to the owner <paramref name="owner"/> names
    /// (null: no event for that row). The events are thin, the id only; they go out after the write commits, at least
    /// once, and never twice to one destination.
    /// </summary>
    /// <param name="name">The event prefix; the type name in lower case by default (<c>report</c>).</param>
    public AppsBuilder AddEntityEvents<T>(EntityEvents events, string? permission, Func<T, WebhookOwner?> owner, string? name = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        if ((events & EntityEvents.All) == 0)
            throw new ArgumentException("Name at least one of Created, Updated, Deleted.", nameof(events));
        var prefix = name ?? typeof(T).Name.ToLowerInvariant();
        if (events.HasFlag(EntityEvents.Created))
            Events.Add(new($"{prefix}.created", typeof(EntityCreated<T>), permission, $"A {typeof(T).Name} was created."));
        if (events.HasFlag(EntityEvents.Updated))
            Events.Add(new($"{prefix}.updated", typeof(EntityUpdated<T>), permission, $"A {typeof(T).Name} was changed."));
        if (events.HasFlag(EntityEvents.Deleted))
            Events.Add(new($"{prefix}.deleted", typeof(EntityDeleted<T>), permission, $"A {typeof(T).Name} was deleted."));
        EntityEventRegistrations.Add(new(typeof(T), events, entity => owner((T)entity), typeof(Services.EntityWebhookInterceptor<T>)));
        return this;
    }

    /// <summary>Another kind of webhook owner (D18), e.g. CodeCoverage's GitHub accounts. Users and organizations are built in.</summary>
    public AppsBuilder AddOwnerProvider<TProvider>() where TProvider : class, IWebhookOwnerProvider
    {
        OwnerProviders.Add(typeof(TProvider));
        return this;
    }

    /// <summary>
    /// An extra signature on every delivery, next to the endpoint's own <c>v1</c>. For <c>v2a</c> (D21) pass
    /// <c>new Ed25519WebhookSigner(WebhookSigningKey.Parse(config["…"]))</c> from MintPlayer.Webhooks.Ed25519, and its
    /// <c>whpk_</c> public key, which the settings page shows receivers.
    /// </summary>
    public AppsBuilder AddSigner(IWebhookSigner signer, string? publicKey = null)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (signer.Version == HmacWebhookSigner.SchemeVersion)
            throw new ArgumentException("v1 is signed with each endpoint's own secret already.", nameof(signer));
        Signers.Add((signer, publicKey));
        return this;
    }

    internal bool AppsEnabled { get; private set; }

    /// <summary>
    /// Lets users register Apps (D5, D29): an OAuth client of this site's identity provider with its own webhook, which
    /// other users install. Needs <c>spark.AddIdentityProvider()</c>; direct webhooks work without it.
    /// </summary>
    public AppsBuilder EnableApps()
    {
        AppsEnabled = true;
        return this;
    }

    internal Dictionary<string, List<string>> PermissionGroups { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What an installation token may do on this site (D17): a token holding <paramref name="permission"/> (or the
    /// <c>write</c> that covers a <c>read</c>) is a member of <paramref name="groups"/> in <c>security.json</c>. A permission
    /// mapped to nothing grants no rights here; the App still receives its events.
    /// </summary>
    public AppsBuilder MapPermission(string permission, params string[] groups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (!PermissionGroups.TryGetValue(permission, out var list))
            PermissionGroups[permission] = list = [];
        list.AddRange(groups.Where(g => !string.IsNullOrWhiteSpace(g)));
        return this;
    }

    public AppsBuilder ConfigureOptions(Action<SparkAppsOptions> configure)
    {
        Configure.Add(configure);
        return this;
    }
}
