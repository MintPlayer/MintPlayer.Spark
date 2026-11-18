using System.Reflection;
using MintPlayer.Spark.Apps.Catalog;
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

    public AppsBuilder ConfigureOptions(Action<SparkAppsOptions> configure)
    {
        Configure.Add(configure);
        return this;
    }
}
