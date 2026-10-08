using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.IdentityProvider.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.CustomActions;

/// <summary>
/// Generates a client secret and shows it once (<c>docs/identity_provider_platform_PRD.md</c> D5).
/// Admins only. The plaintext leaves the server in this response and nowhere else: only its hash is
/// stored, so a lost secret is replaced, never recovered. Several secrets can coexist, so a rotation
/// generates the new one, deploys it, then revokes the old.
/// </summary>
internal sealed partial class GenerateSecretAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || await session.LoadAsync<OidcApplication>(id, cancellationToken) is not { } app) return;
        if (!await access.IsApplicationAdminAsync(app))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.adminOnly"), NotificationKind.Error);
            return;
        }
        if (string.Equals(app.ClientType, "public", StringComparison.OrdinalIgnoreCase))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.publicClientNoSecret"), NotificationKind.Error);
            return;
        }

        var plaintext = OpaqueHandle.Generate();
        var secret = new ClientSecret
        {
            SecretId = Guid.NewGuid().ToString("N")[..12],
            Hash = ClientSecretHasher.Hash(plaintext),
            Description = $"Generated {DateTime.UtcNow:yyyy-MM-dd}",
            CreatedAt = DateTime.UtcNow,
        };
        app.Secrets.Add(secret);
        await audit.RecordAsync(session, OidcAuditKinds.SecretGenerated, access.UserId, app.Id, ipAddress: access.IpAddress,
            details: new Dictionary<string, string> { ["secretId"] = secret.SecretId }, ct: cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        manager.Client.ShowSecret(
            manager.GetTranslatedMessage("identityProvider.secretTitle"),
            manager.GetTranslatedMessage("identityProvider.secretShownOnce"),
            plaintext);
        manager.Client.RefreshQuery(OidcApplicationSecretRowActions.QueryAlias);
    }
}

/// <summary>Revokes the selected secrets at once (D5). Admins only.</summary>
internal sealed partial class RevokeSecretAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (await OidcTeam.LoadAsync(args, session, cancellationToken) is not { } app) return;
        if (!await access.IsApplicationAdminAsync(app))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.adminOnly"), NotificationKind.Error);
            return;
        }

        var ids = args.SelectedItems.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var revoked = app.Secrets.Where(s => s.SecretId is not null && ids.Contains(s.SecretId)).ToList();
        foreach (var secret in revoked)
        {
            app.Secrets.Remove(secret);
            await audit.RecordAsync(session, OidcAuditKinds.SecretRevoked, access.UserId, app.Id, ipAddress: access.IpAddress,
                details: new Dictionary<string, string> { ["secretId"] = secret.SecretId! }, ct: cancellationToken);
        }

        if (revoked.Count == 0) return;
        await session.SaveChangesAsync(cancellationToken);
        manager.Client.RefreshQuery(OidcApplicationSecretRowActions.QueryAlias);
    }
}
