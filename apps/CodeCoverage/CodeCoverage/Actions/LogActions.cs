using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Logging.Models;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// The bot's logs on this server (#660 G14, G16): the library's rule (a log opens exactly when its related record does)
/// plus two narrowings of CodeCoverage's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list is the Administrators' alone.</b> <c>security.json</c> grants <c>Read/Log</c> to everyone so a "view log"
/// link on a command opens for whoever may read the command, and Read implies Query in Spark, so the grant cannot say
/// "admins only" for the list. <see cref="ListAll"/> can: only the Administrators hold it, and Query is withheld from
/// everybody else, on the grid and on each row.
/// </para>
/// <para>
/// <b>An exception's text is for the repository's managers.</b> A logged exception can carry paths, URLs and forge
/// answers; <c>Entries.Exception</c> is blanked unless the caller manages the related record's repository (a manager of its
/// account or an administrator of the repository on its forge, <see cref="ISparkVisibility.CanManageRepositoryAsync"/>).
/// A log whose repository cannot be found keeps it hidden.
/// </para>
/// </remarks>
public partial class LogActions : MintPlayer.Spark.Logging.Actions.LogActions
{
    /// <summary>The right that opens the <c>Logs</c> list (<c>ListAll/Log</c>), granted to the Administrators only.</summary>
    public const string ListAll = "ListAll";

    /// <summary>The attribute blanked for callers who do not manage the log's repository.</summary>
    public const string ExceptionAttribute = "Entries.Exception";

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession documents;

    public override async Task<bool> IsAllowedAsync(string action, Log entity)
        => (action != SparkCoreActions.Query || await permissions.IsAllowedAsync(ListAll, nameof(Log)))
            && await base.IsAllowedAsync(action, entity);

    public override async Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        await base.OnDisableActionsAsync(target, context);
        if (context.TargetKind == DisableActionsTargetKind.Query && !await permissions.IsAllowedAsync(ListAll, nameof(Log)))
            target.DisableActions(SparkCoreActions.Query);
    }

    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Log entity)
        => await RepositoryOfAsync(entity.RelatedPersistentObjectId) is { } repository && await visibility.CanManageRepositoryAsync(repository)
            ? null
            : [ExceptionAttribute];

    /// <summary>The repository a log belongs to: its related record's (a command's), or the record itself (a merge-queue run, a backport).</summary>
    private async Task<Repository?> RepositoryOfAsync(string? related)
    {
        if (related is not { Length: > 0 }) return null;
        if (related.StartsWith(CommandInvocation.Prefix, StringComparison.Ordinal))
            return await documents.LoadAsync<CommandInvocation>(related) is { } command ? await documents.LoadAsync<Repository>(command.Repository) : null;
        return related.StartsWith("Repositories/", StringComparison.Ordinal) ? await documents.LoadAsync<Repository>(related) : null;
    }
}
