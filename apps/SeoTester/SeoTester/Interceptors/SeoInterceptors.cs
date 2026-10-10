using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using SeoTester.Catalogue;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Sites;

namespace SeoTester.Interceptors;

/// <summary>
/// Every persistent-object write of a <see cref="Site"/> (<c>docs/seo_analyzer_PRD.md</c> F2, F3):
/// <list type="bullet">
/// <item><b>Create:</b> the origin is normalized (or refused), an empty organization is the one chosen in the switcher,
/// else the creator's personal one (the organizations package, D21, Q-M11a), a second site with the same origin in one organization is refused, and the server-owned fields start
/// fresh: a new verification token, unverified, no latest run.</item>
/// <item><b>Edit:</b> only the profile changes. The origin, the organization, the verification and the latest run keep
/// their stored values whatever the client posted, the way <c>ExpiresAtUtc</c> and <c>ShareTokenHash</c> are kept (D41):
/// a Member can neither verify a site by posting <c>IsVerified</c> nor move a verified site to another organization.
/// Only server code (raw sessions) changes them.</item>
/// </list>
/// </summary>
public sealed partial class SiteSaveInterceptor : IBeforeSave<Site>
{
    /// <summary>After the organizations package's default (registered later, D21), which the duplicate check reads.</summary>
    public InterceptorStage Stage => InterceptorStage.Finalize;

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly TimeProvider time;

    public async ValueTask OnBeforeSaveAsync(Site site, SaveContext context)
    {
        if (context.Before is Site stored)
        {
            KeepServerFields(site, stored);
            return;
        }

        if (!SiteOrigin.TryNormalize(site.Origin, out var origin, out var host, out var problem))
            throw new SparkValidationException(problem!);

        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        site.Origin = origin;
        site.Host = host;
        if (site.Monitoring is null)
            site.Monitoring = MonitoringSchedule.Off;

        site.VerificationToken = OwnershipProof.NewToken();
        site.IsVerified = false;
        site.VerificationMethod = null;
        site.VerifiedAtUtc = null;
        site.VerificationCheckedAtUtc = null;
        site.VerificationFailures = 0;
        site.LatestRunId = null;
        site.LatestScore = null;
        site.LatestGrade = null;
        site.LatestRunAtUtc = null;
        site.CreatedBy = userId;
        site.CreatedAtUtc = time.GetUtcNow().UtcDateTime;

        var organizationId = site.OrganizationId;
        var exists = await session.Query<Site>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .AnyAsync(s => s.OrganizationId == organizationId && s.Origin == origin);
        if (exists)
            throw new SparkValidationException($"{origin} is already one of this organization's sites.");
    }

    private static void KeepServerFields(Site site, Site stored)
    {
        site.Origin = stored.Origin;
        site.Host = stored.Host;
        site.OrganizationId = stored.OrganizationId;
        site.VerificationToken = stored.VerificationToken;
        site.IsVerified = stored.IsVerified;
        site.VerificationMethod = stored.VerificationMethod;
        site.VerifiedAtUtc = stored.VerifiedAtUtc;
        site.VerificationCheckedAtUtc = stored.VerificationCheckedAtUtc;
        site.VerificationFailures = stored.VerificationFailures;
        site.LatestRunId = stored.LatestRunId;
        site.LatestScore = stored.LatestScore;
        site.LatestGrade = stored.LatestGrade;
        site.LatestRunAtUtc = stored.LatestRunAtUtc;
        site.CreatedBy = stored.CreatedBy;
        site.CreatedAtUtc = stored.CreatedAtUtc;
    }
}

/// <summary>
/// Catalogue edits through the generic UI (D32, administrators): a new entry takes its id from its check id
/// (<c>CheckDefinitions/{CheckId}</c>, refused when malformed or taken); an edit keeps the check id, which C#
/// implementations are keyed by, and the seeder's hash, so an edit reads as an edit; weights are never negative.
/// </summary>
public sealed partial class CheckDefinitionSaveInterceptor : IBeforeSave<CheckDefinition>
{
    /// <summary>After the organizations package's default (registered later, D21), which the duplicate check reads.</summary>
    public InterceptorStage Stage => InterceptorStage.Finalize;

    [Inject] private readonly IAsyncDocumentSession session;

    public async ValueTask OnBeforeSaveAsync(CheckDefinition definition, SaveContext context)
    {
        if (definition.Weight < 0)
            throw new SparkValidationException("A weight cannot be negative.");

        if (context.Before is CheckDefinition stored)
        {
            definition.CheckId = stored.CheckId;
            definition.SeededHash = stored.SeededHash;
            return;
        }

        definition.CheckId = definition.CheckId?.Trim() ?? string.Empty;
        if (!ShippedCatalogue.IsCheckId(definition.CheckId))
            throw new SparkValidationException("A check id is '<category>.<name>' in lower case, e.g. 'seo.title-present'.");
        definition.Id = CheckDefinition.IdFor(definition.CheckId);
        definition.SeededHash = null;
        if (await session.Advanced.ExistsAsync(definition.Id))
            throw new SparkValidationException($"The check '{definition.CheckId}' already exists.");
    }
}
