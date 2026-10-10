using MintPlayer.Spark.Http;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Settings;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using SeoTester.Audits;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Settings;
using SeoTester.Sites;

namespace SeoTester.Verification;

/// <summary>What <see cref="SiteVerification.VerifyAsync"/> found.</summary>
public enum VerifyOutcome
{
    /// <summary>The token was found: the site is verified for its organization.</summary>
    Verified,

    /// <summary>No proof was found; nothing changed but the check time.</summary>
    NoProof,

    /// <summary>
    /// The proof was found, but another organization holds the site verified (D18, D42): its Owners got a mailed
    /// request to let the caller in. This organization's site stays unverified.
    /// </summary>
    ClaimRequested,

    /// <summary>As <see cref="ClaimRequested"/>, but the caller is already a member there: nothing was mailed.</summary>
    HeldByCallersOtherOrganization,
}

/// <summary>The outcome, the method that proved it, and the holding organization's name for a claim.</summary>
public sealed record VerifyResult(VerifyOutcome Outcome, string? Method, string? HolderOrganizationId);

/// <summary>
/// Ownership verification per organization (<c>docs/seo_analyzer_PRD.md</c> F3, D18, D29, D42): the Verify action and
/// the periodic re-check. Writes go through raw sessions: the verification fields are the server's
/// (<see cref="Interceptors.SiteSaveInterceptor"/> keeps them on every persistent-object write).
/// </summary>
public sealed partial class SiteVerification(
    IDocumentStore store,
    IDnsTxtResolver dns,
    IHttpClientFactory httpClients,
    ISparkOrganizations organizations,
    ISparkSettings settings,
    AuditLog auditLog,
    TimeProvider time,
    ILogger<SiteVerification> logger)
{
    /// <summary>The SSRF-safe client for the file and meta-tag proofs: redirects only within the site's origin.</summary>
    public const string HttpClientName = "SeoTester.Verification";

    /// <summary>
    /// Looks for the token of <paramref name="siteId"/> and records the outcome. <paramref name="userId"/> asked (an
    /// Admin of the site's organization: the action's row rule).
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(string siteId, string userId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var site = await session.LoadAsync<Site>(siteId, cancellationToken) ?? throw new InvalidOperationException($"No site '{siteId}'.");
        var now = time.GetUtcNow().UtcDateTime;
        var method = await FindProofAsync(site, cancellationToken);
        site.VerificationCheckedAtUtc = now;
        if (method is null)
        {
            await session.SaveChangesAsync(cancellationToken);
            return new VerifyResult(VerifyOutcome.NoProof, null, null);
        }

        // D18: a site another organization already holds verified is claimed, not taken: that organization's Owners
        // decide. The host is what DNS proves, so the same host under http and https is the same site.
        var host = site.Host;
        var siteOrganization = site.OrganizationId;
        var holder = await session.Query<Site>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(s => s.Host == host && s.IsVerified && s.OrganizationId != siteOrganization)
            .FirstOrDefaultAsync(cancellationToken);

        if (holder?.OrganizationId is { } holderOrganization)
        {
            await session.SaveChangesAsync(cancellationToken);
            if (await organizations.GetRoleAsync(holderOrganization, userId, cancellationToken) is not null)
                return new VerifyResult(VerifyOutcome.HeldByCallersOtherOrganization, method, holderOrganization);

            await organizations.RequestToJoinAsync(holderOrganization, userId, OrganizationRole.Member, cancellationToken);
            await auditLog.WriteAsync(AuditLogKinds.SiteClaimRequested, userId, site, runId: null, detail: $"held by {holderOrganization}", cancellationToken);
            return new VerifyResult(VerifyOutcome.ClaimRequested, method, holderOrganization);
        }

        site.IsVerified = true;
        site.VerificationMethod = method;
        site.VerifiedAtUtc = now;
        site.VerificationFailures = 0;
        await session.SaveChangesAsync(cancellationToken);
        await auditLog.WriteAsync(AuditLogKinds.SiteVerified, userId, site, runId: null, detail: method, cancellationToken);
        return new VerifyResult(VerifyOutcome.Verified, method, null);
    }

    /// <summary>
    /// The periodic re-check (F3): verified sites last checked longer than <see cref="SeoSettings.VerificationRecheckInterval"/>
    /// ago, at most <paramref name="batch"/> per pass. A found proof resets the failure count; after
    /// <see cref="SeoSettings.VerificationFailuresBeforeWithdrawal"/> failed checks in a row the verification is withdrawn.
    /// Returns how many sites were checked.
    /// </summary>
    public async Task<int> RecheckDueAsync(int batch, CancellationToken cancellationToken)
    {
        var interval = await settings.GetAsync(SeoSettings.VerificationRecheckInterval, cancellationToken);
        var failuresAllowed = Math.Max(1, await settings.GetAsync(SeoSettings.VerificationFailuresBeforeWithdrawal, cancellationToken));
        var now = time.GetUtcNow().UtcDateTime;
        var due = now - interval;

        using var session = store.OpenAsyncSession();
        var sites = await session.Query<Site>()
            .Where(s => s.IsVerified && (s.VerificationCheckedAtUtc == null || s.VerificationCheckedAtUtc <= due))
            .OrderBy(s => s.VerificationCheckedAtUtc)
            .Take(batch)
            .ToListAsync(cancellationToken);

        var withdrawn = new List<Site>();
        foreach (var site in sites)
        {
            var method = await FindProofAsync(site, cancellationToken);
            site.VerificationCheckedAtUtc = now;
            if (method is not null)
            {
                site.VerificationMethod = method;
                site.VerificationFailures = 0;
                continue;
            }

            site.VerificationFailures++;
            if (site.VerificationFailures >= failuresAllowed)
            {
                site.IsVerified = false;
                site.VerifiedAtUtc = null;
                site.VerificationMethod = null;
                withdrawn.Add(site);
            }
        }
        await session.SaveChangesAsync(cancellationToken);

        foreach (var site in withdrawn)
        {
            logger.LogInformation("Withdrew the verification of {Site} ({Origin}) after {Failures} failed checks.", site.Id, site.Origin, site.VerificationFailures);
            await auditLog.WriteAsync(AuditLogKinds.VerificationWithdrawn, userId: null, site, runId: null, detail: $"{site.VerificationFailures} failed checks", cancellationToken);
        }
        return sites.Count;
    }

    /// <summary>The method whose proof is in place, the site's last method first; null when none is.</summary>
    internal async Task<string?> FindProofAsync(Site site, CancellationToken cancellationToken)
    {
        if (!OwnershipProof.IsToken(site.VerificationToken) || string.IsNullOrEmpty(site.Host) || string.IsNullOrEmpty(site.Origin))
            return null;
        var token = site.VerificationToken!;

        string[] methods = [VerificationMethod.Dns, VerificationMethod.WellKnownFile, VerificationMethod.MetaTag];
        foreach (var method in methods.OrderBy(m => m == site.VerificationMethod ? 0 : 1))
        {
            var found = method switch
            {
                VerificationMethod.Dns => OwnershipProof.MatchesTxt(await dns.GetTxtRecordsAsync(OwnershipProof.RecordName(site.Host), cancellationToken), token),
                VerificationMethod.WellKnownFile => OwnershipProof.MatchesWellKnown(await FetchAsync(site.Origin + OwnershipProof.WellKnownPath, cancellationToken), token),
                _ => OwnershipProof.MatchesMeta(await FetchAsync(site.Origin + "/", cancellationToken), token),
            };
            if (found)
                return method;
        }
        return null;
    }

    /// <summary>A 200's body as text, at most <see cref="OwnershipProof.MaxBytes"/>; null for anything else, refusals included.</summary>
    private async Task<string?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[OwnershipProof.MaxBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (n == 0) break;
                read += n;
            }
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is HttpRequestException or SafeHttpBlockedException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Verification fetch of {Url} failed: {Message}", url, ex.Message);
            return null;
        }
    }
}
