using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Sharing;

namespace MintPlayer.Spark.Sharing;

/// <summary>
/// <c>ShareLink</c> (X6): makes a new link to the object on the detail page, replacing any earlier one, and shows it
/// once. Who may: the right <c>ShareLink/{Type}</c> and the type's row rules for the action <c>ShareLink</c>
/// (D31: organization Owners and Admins, through <c>[RequiresOrganizationRole("ShareLink", OrganizationRole.Admin)]</c>).
/// </summary>
internal sealed partial class ShareLinkAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkShareLinks links;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent is not { Id: { Length: > 0 } id, Name: var typeName })
            return;
        if (!await links.IsEnabledAsync(typeName, id, cancellationToken))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("share.disabled"), NotificationKind.Error);
            return;
        }

        var token = await links.CreateAsync(typeName, id, cancellationToken);
        manager.Client.ShowSecret(
            manager.GetTranslatedMessage("share.linkTitle"),
            manager.GetTranslatedMessage("share.linkShownOnce"),
            links.LinkFor(typeName, id, token, httpContextAccessor.HttpContext));
    }
}

/// <summary><c>RevokeShareLink</c> (X6): the object's link stops working at once.</summary>
internal sealed partial class RevokeShareLinkAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkShareLinks links;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent is not { Id: { Length: > 0 } id, Name: var typeName })
            return;
        await links.RevokeAsync(typeName, id, cancellationToken);
        manager.Client.Notify(manager.GetTranslatedMessage("share.revoked"), NotificationKind.Success);
    }
}

/// <summary>
/// <see cref="ISparkShareable.ShareTokenHash"/> is the framework's, like <c>ExpiresAtUtc</c> (D41): a create starts
/// without a link and an edit keeps the stored one, whatever the client posted.
/// </summary>
internal sealed class ShareTokenInterceptor : IBeforeSave
{
    public bool AppliesTo(Type entityType) => typeof(ISparkShareable).IsAssignableFrom(entityType);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Entity is ISparkShareable entity)
            entity.ShareTokenHash = context.Before is ISparkShareable stored ? stored.ShareTokenHash : null;
        return ValueTask.CompletedTask;
    }
}
