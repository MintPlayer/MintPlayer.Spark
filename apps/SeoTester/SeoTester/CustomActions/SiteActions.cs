using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;
using SeoTester.Audits;
using SeoTester.Entities;
using SeoTester.Sites;
using SeoTester.Verification;

namespace SeoTester.CustomActions;

/// <summary>
/// <c>StartAudit</c> on a site (F4): starts an audit, or joins the one running for its origin (D17). Members and up
/// (the site's row rule); quotas, the cool-down and the gates are <see cref="AuditStarter"/>'s. The result is
/// <c>{ runId, joined }</c>.
/// </summary>
public partial class StartAuditAction : SparkCustomAction
{
    [Inject] private readonly AuditStarter starter;
    [Inject] private readonly MintPlayer.Spark.Services.IModelLoader models;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IManager manager;

    public override Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
        => SiteActionSupport.StartAsync(args, starter, session, currentUser, manager, models, fullCrawl: false, cancellationToken);
}

/// <summary><c>StartFullCrawl</c> on a verified site: the weekly full-site crawl (D14).</summary>
public partial class StartFullCrawlAction : SparkCustomAction
{
    [Inject] private readonly AuditStarter starter;
    [Inject] private readonly MintPlayer.Spark.Services.IModelLoader models;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IManager manager;

    public override Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
        => SiteActionSupport.StartAsync(args, starter, session, currentUser, manager, models, fullCrawl: true, cancellationToken);
}

/// <summary>
/// <c>VerifySite</c> (F3, Admins): looks for the organization's token in DNS, the well-known file and the home page's
/// meta tag. A site another organization holds verified sends that organization's Owners a join request (D18, D42).
/// The result is <c>{ outcome, method }</c>.
/// </summary>
public partial class VerifySiteAction : SparkCustomAction
{
    [Inject] private readonly SiteVerification verification;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        var site = await SiteActionSupport.SiteAsync(args, session, cancellationToken);
        var userId = SiteActionSupport.UserId(currentUser);
        var result = await verification.VerifyAsync(site.Id!, userId, cancellationToken);
        args.SetResult(new { outcome = result.Outcome.ToString(), method = result.Method });

        var token = site.VerificationToken ?? string.Empty;
        switch (result.Outcome)
        {
            case VerifyOutcome.Verified:
                manager.Client.Notify(manager.GetTranslatedMessage("seo.verified", site.Origin), NotificationKind.Success);
                break;
            case VerifyOutcome.ClaimRequested:
                manager.Client.Notify(manager.GetTranslatedMessage("seo.claimRequested", site.Origin), NotificationKind.Info);
                break;
            case VerifyOutcome.HeldByCallersOtherOrganization:
                manager.Client.Notify(manager.GetTranslatedMessage("seo.heldByYourOtherOrganization", site.Origin), NotificationKind.Info);
                break;
            default:
                manager.Client.Notify(manager.GetTranslatedMessage("seo.noProof",
                    OwnershipProof.RecordName(site.Host), OwnershipProof.ExpectedValue(token), site.Origin + OwnershipProof.WellKnownPath, OwnershipProof.MetaTag(token)), NotificationKind.Error);
                break;
        }
    }
}

internal static class SiteActionSupport
{
    public static async Task StartAsync(CustomActionArgs args, AuditStarter starter, IAsyncDocumentSession session, ISparkCurrentUser currentUser, IManager manager, MintPlayer.Spark.Services.IModelLoader models, bool fullCrawl, CancellationToken cancellationToken)
    {
        var site = await SiteAsync(args, session, cancellationToken);
        var result = await starter.StartAsync(site, UserId(currentUser), fullCrawl, cancellationToken);
        args.SetResult(new { runId = result.RunId, joined = result.Joined });
        manager.Client.Notify(manager.GetTranslatedMessage(result.Joined ? "seo.auditJoined" : "seo.auditStarted", site.Origin), NotificationKind.Success);
        // M11: the caller follows the run live on its page (a joined run is the one already in progress).
        if (models.GetEntityTypeByName(nameof(AuditRun)) is { } runType)
            manager.Client.Navigate(runType.Id, result.RunId);
    }

    /// <summary>The site of the detail page; the parent was re-loaded and row-checked for the action by the framework.</summary>
    public static async Task<Site> SiteAsync(CustomActionArgs args, IAsyncDocumentSession session, CancellationToken cancellationToken)
        => args.Parent?.Id is { Length: > 0 } id && await session.LoadAsync<Site>(id, cancellationToken) is { } site
            ? site
            : throw new SparkValidationException("Open the site first.");

    /// <summary>
    /// The caller's user id: the sign-in cookie's, or an AI agent's (its bearer token carries the local id as <c>sub</c>,
    /// which <see cref="ISparkCurrentUser"/> reads; MCP PRD D4). The scan and its quota are the user's either way.
    /// </summary>
    public static string UserId(ISparkCurrentUser currentUser)
        => currentUser.Id is { Length: > 0 } id
            ? id
            : throw new SparkValidationException("Sign in first.");
}
