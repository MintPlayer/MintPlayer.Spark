using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>An installation as its target's managers and the App's managers see it.</summary>
/// <param name="PendingPermissions">What the App asks for beyond <paramref name="Permissions"/>; empty when nothing waits to be accepted.</param>
public sealed record AppInstallationView(
    string Id, string AppSlug, string AppName, string TargetKind, string TargetKey, IReadOnlyList<string> Permissions,
    IReadOnlyList<string> PendingPermissions, IReadOnlyList<string> Events, bool Suspended, string InstalledBy, DateTime InstalledUtc);

/// <summary>A place the user may install the App on, and whether it is there already.</summary>
public sealed record AppInstallTarget(string Kind, string Key, string DisplayName, string? InstallationId);

/// <summary>The consent screen (§4.3 flow 6): what the App asks for, and where the user may install it.</summary>
public sealed record AppInstallPrompt(SparkAppPublicView App, IReadOnlyList<AppInstallTarget> Targets);

/// <summary>A new installation, and where to send the installer next (the App's setup URL, with <c>installation_id</c>).</summary>
public sealed record AppInstalled(AppInstallationView Installation, string? RedirectUrl);

public sealed record AppInstallInput(string? Slug, string? TargetKind, string? TargetKey);

/// <summary>
/// Installations (D30): a user installs an App on themselves or on an organization they administer, granting what the App
/// asks for at that moment. The App learns of every change through <c>installation.*</c> events on its webhook, which it
/// receives whatever events it subscribed to. Each installation holds an identity provider grant; its tokens die with
/// the grant (suspend withdraws it, uninstall revokes it).
/// </summary>
internal sealed partial class AppInstallationManager
{
    private static readonly Regex InstallationIdShape = new("^in_[0-9a-f]{24}$", RegexOptions.CultureInvariant);
    private static readonly Regex SlugShape = new("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$", RegexOptions.CultureInvariant);

    public const string CreatedEvent = "installation.created";
    public const string DeletedEvent = "installation.deleted";
    public const string SuspendEvent = "installation.suspend";
    public const string UnsuspendEvent = "installation.unsuspend";
    public const string PermissionsAcceptedEvent = "installation.new_permissions_accepted";

    /// <summary>The request scope's session: each call is one unit of work, which the scheduler joins (#536 D50).</summary>
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly WebhookOwnerResolver owners;
    [Inject] private readonly WebhookScheduler scheduler;
    [Inject] private readonly Catalog.WebhookEventCatalog catalog;
    [Inject] private readonly IOptions<SparkAppsOptions> options;
    [Inject] private readonly TimeProvider time;

    public static bool IsInstallationId(string? id) => id is not null && InstallationIdShape.IsMatch(id);

    public async Task<WebhookResult<AppInstallPrompt>> PromptAsync(string? slug, string userId, CancellationToken cancellationToken)
    {
        if (slug is null || !SlugShape.IsMatch(slug))
            return WebhookResult<AppInstallPrompt>.NotFound();
        if (await session.LoadAsync<SparkApp>(SparkApp.IdFor(slug).Value, cancellationToken) is not { } app)
            return WebhookResult<AppInstallPrompt>.NotFound();

        var manageable = (await owners.ListManageableAsync(userId, cancellationToken))
            .Where(t => app.Public || (t.Kind == app.OwnerKind && t.Key == app.OwnerKey))
            .ToList();
        if (manageable.Count == 0)
            return WebhookResult<AppInstallPrompt>.NotFound();

        var registries = await session.LoadAsync<WebhookOwnerRegistry>(manageable.Select(t => WebhookOwnerRegistry.IdFor(new(t.Kind, t.Key)).Value), cancellationToken);
        var targets = new List<AppInstallTarget>();
        foreach (var target in manageable)
        {
            var registry = registries.GetValueOrDefault(WebhookOwnerRegistry.IdFor(new(target.Kind, target.Key)).Value);
            var existing = registry?.InstallationIds.FirstOrDefault(id => app.InstallationIds.Contains(id, StringComparer.Ordinal));
            targets.Add(new(target.Kind, target.Key, target.DisplayName, existing?[AppInstallation.IdPrefix.Length..]));
        }
        return WebhookResult<AppInstallPrompt>.Ok(new(PublicView(app), targets));
    }

    public async Task<WebhookResult<AppInstalled>> InstallAsync(AppInstallInput input, string userId, CancellationToken cancellationToken)
    {
        if (input.Slug is null || !SlugShape.IsMatch(input.Slug) || string.IsNullOrEmpty(input.TargetKind) || string.IsNullOrEmpty(input.TargetKey))
            return WebhookResult<AppInstalled>.Invalid("slug, targetKind and targetKey are required.");
        var target = new WebhookOwner(input.TargetKind, input.TargetKey);
        if (!await owners.CanManageAsync(target, userId, cancellationToken))
            return WebhookResult<AppInstalled>.NotFound();

        session.OptimisticConcurrency = true;
        if (await session.LoadAsync<SparkApp>(SparkApp.IdFor(input.Slug).Value, cancellationToken) is not { } app
            || (!app.Public && app.Owner != target))
            return WebhookResult<AppInstalled>.NotFound();

        var registryId = WebhookOwnerRegistry.IdFor(target);
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(registryId.Value, cancellationToken);
        if (registry is null)
        {
            registry = new WebhookOwnerRegistry { Id = registryId, OwnerKind = target.Kind, OwnerKey = target.Key };
            await session.StoreAsync(registry, registryId.Value, cancellationToken);
        }
        if (registry.InstallationIds.Any(id => app.InstallationIds.Contains(id, StringComparer.Ordinal)))
            return WebhookResult<AppInstalled>.Conflict($"The App '{app.Slug}' is installed there already.");
        if (registry.InstallationIds.Count >= options.Value.MaxInstallationsPerOwner)
            return WebhookResult<AppInstalled>.Invalid($"An owner has at most {options.Value.MaxInstallationsPerOwner} Apps installed.");

        var installation = new AppInstallation
        {
            Id = AppInstallation.IdFor("in_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))),
            AppSlug = app.Slug,
            TargetKind = target.Kind,
            TargetKey = target.Key,
            Permissions = [.. app.Permissions],
            InstalledBy = userId,
            InstalledUtc = time.GetUtcNow().UtcDateTime,
        };
        installation.GrantId = await OidcMachineClients.CreateGrantAsync(session, app.OidcApplicationId, Subject(installation), installation.Permissions, cancellationToken);
        await session.StoreAsync(installation, string.Empty, installation.Id.Value, cancellationToken);
        registry.InstallationIds.Add(installation.Id.Value);
        // The App document is part of the uniqueness check: two installs of one App on one target race on both.
        app.InstallationIds.Add(installation.Id.Value);
        await NotifyAppAsync(app, installation, CreatedEvent, userId, cancellationToken);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (SparkStoreConcurrencyException)
        {
            return WebhookResult<AppInstalled>.Conflict("The App or the target changed meanwhile; try again.");
        }
        var redirect = app.SetupUrl is { } setup
            ? setup + (setup.Contains('?') ? "&" : "?") + "installation_id=" + Uri.EscapeDataString(installation.PublicId) + "&setup_action=install"
            : null;
        return WebhookResult<AppInstalled>.Ok(new(View(installation, app), redirect));
    }

    /// <summary>The Apps installed on a target, to its managers.</summary>
    public async Task<WebhookResult<IReadOnlyList<AppInstallationView>>> ListForTargetAsync(string? targetKind, string? targetKey, string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(targetKind) || string.IsNullOrEmpty(targetKey))
            return WebhookResult<IReadOnlyList<AppInstallationView>>.Invalid("targetKind and targetKey are required.");
        var target = new WebhookOwner(targetKind, targetKey);
        if (!await owners.CanManageAsync(target, userId, cancellationToken))
            return WebhookResult<IReadOnlyList<AppInstallationView>>.NotFound();
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(target).Value, cancellationToken);
        return WebhookResult<IReadOnlyList<AppInstallationView>>.Ok(await ViewsAsync(registry?.InstallationIds ?? [], cancellationToken));
    }

    /// <summary>An App's installations, to the App's managers (they need the ids for tokens).</summary>
    public async Task<WebhookResult<IReadOnlyList<AppInstallationView>>> ListForAppAsync(string? slug, string userId, CancellationToken cancellationToken)
    {
        if (slug is null || !SlugShape.IsMatch(slug))
            return WebhookResult<IReadOnlyList<AppInstallationView>>.NotFound();
        if (await session.LoadAsync<SparkApp>(SparkApp.IdFor(slug).Value, cancellationToken) is not { } app
            || !await owners.CanManageAsync(app.Owner, userId, cancellationToken))
            return WebhookResult<IReadOnlyList<AppInstallationView>>.NotFound();
        return WebhookResult<IReadOnlyList<AppInstallationView>>.Ok(await ViewsAsync(app.InstallationIds, cancellationToken));
    }

    /// <summary>
    /// Suspends or resumes an installation. Suspending ends the tokens it holds at once (the grant is withdrawn) and
    /// stops its events; resuming does not bring those tokens back.
    /// </summary>
    public Task<WebhookResult<AppInstallationView>> SetSuspendedAsync(string? id, bool suspended, string userId, CancellationToken cancellationToken)
        => WithInstallationAsync(id, userId, async (installation, app) =>
        {
            if (installation.Suspended == suspended)
                return WebhookResult<AppInstallationView>.Ok(View(installation, app));
            installation.Suspended = suspended;
            installation.SuspendedUtc = suspended ? time.GetUtcNow().UtcDateTime : null;
            if (suspended)
                await OidcMachineClients.WithdrawGrantAsync(session, installation.GrantId, revoke: false, cancellationToken);
            await NotifyAppAsync(app, installation, suspended ? SuspendEvent : UnsuspendEvent, userId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<AppInstallationView>.Ok(View(installation, app));
        }, cancellationToken);

    /// <summary>The installer accepts what the App asks for now (GitHub's "Review request").</summary>
    public Task<WebhookResult<AppInstallationView>> AcceptPermissionsAsync(string? id, string userId, CancellationToken cancellationToken)
        => WithInstallationAsync(id, userId, async (installation, app) =>
        {
            if (Pending(installation, app).Count == 0)
                return WebhookResult<AppInstallationView>.Ok(View(installation, app));
            installation.Permissions = [.. app.Permissions];
            await NotifyAppAsync(app, installation, PermissionsAcceptedEvent, userId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<AppInstallationView>.Ok(View(installation, app));
        }, cancellationToken);

    /// <summary>Removes the installation; its grant is revoked, so every token it holds stops working.</summary>
    public Task<WebhookResult<AppInstallationView>> UninstallAsync(string? id, string userId, CancellationToken cancellationToken)
        => WithInstallationAsync(id, userId, async (installation, app) =>
        {
            await RemoveAsync(session, installation, app, cancellationToken);
            await NotifyAppAsync(app, installation, DeletedEvent, userId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<AppInstallationView>.Ok(View(installation, app));
        }, cancellationToken);

    /// <summary>
    /// The App asks for less: each installation drops what the App no longer asks for, at once, and the tokens issued
    /// under the wider grant end. Asking for more changes nothing until the installer accepts. In the caller's session.
    /// </summary>
    internal static async Task NarrowAsync(ISparkSession session, SparkApp app, CancellationToken cancellationToken)
    {
        if (app.InstallationIds.Count == 0)
            return;
        foreach (var installation in (await session.LoadAsync<AppInstallation>(app.InstallationIds, cancellationToken)).Values)
        {
            if (installation is null)
                continue;
            var kept = installation.Permissions.Where(p => app.Permissions.Contains(p, StringComparer.Ordinal)).ToList();
            if (kept.Count == installation.Permissions.Count)
                continue;
            installation.Permissions = kept;
            await OidcMachineClients.WithdrawGrantAsync(session, installation.GrantId, revoke: false, cancellationToken);
        }
    }

    /// <summary>Removes every installation of an App that is being deleted (its client's grants go with the client). In the caller's session.</summary>
    internal static async Task RemoveAllAsync(ISparkSession session, SparkApp app, CancellationToken cancellationToken)
    {
        if (app.InstallationIds.Count == 0)
            return;
        foreach (var installation in (await session.LoadAsync<AppInstallation>(app.InstallationIds.ToList(), cancellationToken)).Values)
        {
            if (installation is not null)
                await RemoveAsync(session, installation, app, cancellationToken);
        }
    }

    private static async Task RemoveAsync(ISparkSession session, AppInstallation installation, SparkApp app, CancellationToken cancellationToken)
    {
        await OidcMachineClients.WithdrawGrantAsync(session, installation.GrantId, revoke: true, cancellationToken);
        // Ignoring case: an installation listed before #536 is "AppInstallations/…" there, its loaded id lower case.
        var id = installation.Id.Value;
        if (await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(installation.Target).Value, cancellationToken) is { } registry)
            registry.InstallationIds.RemoveAll(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
        app.InstallationIds.RemoveAll(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
        session.Delete(installation);
    }

    internal static string Subject(AppInstallation installation) => "installation:" + installation.PublicId;

    internal static List<string> Pending(AppInstallation installation, SparkApp app)
        => app.Permissions.Where(p => !SparkAppManager.Covers(installation.Permissions, p)).ToList();

    /// <summary>
    /// An <c>installation.*</c> event on the App's webhook (if it has an enabled one), in the scope's session. It is sent
    /// whatever events the App subscribed to: an App always learns where it is installed.
    /// </summary>
    private async Task NotifyAppAsync(SparkApp app, AppInstallation installation, string type, string userId, CancellationToken cancellationToken)
    {
        if (app.WebhookEndpointId is not { } ep || await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdFor(ep).Value, cancellationToken) is not { Enabled: true } hook)
            return;
        var now = time.GetUtcNow();
        var messageId = WebhookPublisher.NewMessageId(now);
        var data = new JsonObject
        {
            ["installation"] = new JsonObject
            {
                ["id"] = installation.PublicId,
                ["app"] = app.Slug,
                ["target"] = new JsonObject { ["kind"] = installation.TargetKind, ["key"] = installation.TargetKey },
                ["permissions"] = new JsonArray(installation.Permissions.Select(p => (JsonNode?)p).ToArray()),
                ["suspended"] = installation.Suspended,
            },
            ["sender"] = userId,
        };
        var delivery = new WebhookDelivery
        {
            Id = WebhookDelivery.IdFor(hook.PublicId, now.UtcDateTime, messageId),
            MessageId = messageId,
            EventType = type,
            EndpointId = hook.PublicId,
            Payload = WebhookPublisher.Envelope(type, now, data, options.Value.MaxPayloadBytes),
            Status = WebhookDeliveryStatus.Pending,
            CreatedUtc = now.UtcDateTime,
            ExpiresAtUtc = now.UtcDateTime + options.Value.DeliveryRetention,
        };
        await scheduler.ScheduleAsync(delivery, bypassChain: false, cancellationToken);
    }

    private async Task<WebhookResult<AppInstallationView>> WithInstallationAsync(
        string? id, string userId, Func<AppInstallation, SparkApp, Task<WebhookResult<AppInstallationView>>> action, CancellationToken cancellationToken)
    {
        if (!IsInstallationId(id))
            return WebhookResult<AppInstallationView>.NotFound();
        session.OptimisticConcurrency = true;
        var installation = (await session.LoadAsync<AppInstallation>([AppInstallation.IdFor(id!).Value], [nameof(AppInstallation.GrantId)], cancellationToken)).Values.SingleOrDefault();
        if (installation is null || !await owners.CanManageAsync(installation.Target, userId, cancellationToken)
            || await session.LoadAsync<SparkApp>(SparkApp.IdFor(installation.AppSlug).Value, cancellationToken) is not { } app)
            return WebhookResult<AppInstallationView>.NotFound();
        try
        {
            return await action(installation, app);
        }
        catch (SparkStoreConcurrencyException)
        {
            return WebhookResult<AppInstallationView>.Conflict("The installation changed meanwhile; reload and try again.");
        }
    }

    private async Task<IReadOnlyList<AppInstallationView>> ViewsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return [];
        var installations = (await session.LoadAsync<AppInstallation>(ids, cancellationToken)).Values.Where(i => i is not null).ToList();
        var apps = await session.LoadAsync<SparkApp>(installations.Select(i => SparkApp.IdFor(i!.AppSlug).Value).Distinct(), cancellationToken);
        return installations
            .Where(i => apps.GetValueOrDefault(SparkApp.IdFor(i!.AppSlug).Value) is not null)
            .Select(i => View(i!, apps[SparkApp.IdFor(i!.AppSlug).Value]!))
            .OrderBy(v => v.InstalledUtc)
            .ToList();
    }

    /// <summary>The events in effect: the App's whose permission the installation granted (the fan-out's rule).</summary>
    private AppInstallationView View(AppInstallation i, SparkApp app) => new(
        i.PublicId, app.Slug, app.Name, i.TargetKind, i.TargetKey, i.Permissions, Pending(i, app),
        app.Events.Where(e => catalog.Find(e)?.Permission is not { } needed || SparkAppManager.Covers(i.Permissions, needed)).ToList(),
        i.Suspended, i.InstalledBy, i.InstalledUtc);

    private static SparkAppPublicView PublicView(SparkApp app)
        => new(app.Slug, app.Name, app.Description, app.HomepageUrl, app.OwnerKind, app.OwnerKey, app.Permissions, app.Events);
}
