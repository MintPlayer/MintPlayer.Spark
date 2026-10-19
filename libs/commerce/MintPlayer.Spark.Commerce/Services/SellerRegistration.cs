using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Services;

/// <summary>What a seller fills in (D21, GA5, GA6). Null leaves a field unchanged.</summary>
public sealed record SellerProfileInput(
    TraderStatus? TraderStatus,
    TraderData? Trader,
    Dac7Data? Dac7);

/// <summary>
/// Seller registration and the seller's own data (D21, §4.19). Every call checks the caller: seller staff act on their
/// own organization (Admin/Owner), platform admins on any.
/// </summary>
internal sealed class SellerRegistration(
    IDocumentStore store,
    IOptions<SparkCommerceOptions> options,
    CommerceAccess access,
    ISparkApprovals approvals,
    ISparkLegalDocuments legalDocuments,
    ICommerceAudit audit,
    TimeProvider clock,
    ILogger<SellerRegistration> logger)
{
    /// <summary>The profile, if the caller may manage the organization.</summary>
    public async Task<SellerProfile?> GetAsync(string organizationId, CancellationToken ct)
    {
        if (!await access.MayManageSellerAsync(organizationId, ct)) return null;
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(organizationId), ct);
    }

    /// <summary>Creates or updates the profile. Changing trader data after approval sends it back to verification.</summary>
    public async Task<SellerProfile> SaveAsync(string organizationId, SellerProfileInput input, CancellationToken ct)
    {
        await EnsureMayManageAsync(organizationId, ct);
        var o = options.Value;
        using var session = store.OpenAsyncSession();
        var profile = await LoadOrCreateAsync(session, organizationId, ct);

        if (profile.Status == SellerRegistrationStatus.Suspended)
            throw new SparkValidationException("A suspended seller cannot change its profile; file a complaint instead.");

        if (input.TraderStatus is { } status) profile.TraderStatus = status;
        if (input.Trader is { } trader)
        {
            var changed = TraderChanged(profile.Trader, trader);
            trader.HasIdentityDocument = profile.Trader.HasIdentityDocument; // only the upload endpoint sets it
            if (trader.SelfCertification && !profile.Trader.SelfCertification) trader.SelfCertifiedAt = clock.GetUtcNow();
            else trader.SelfCertifiedAt = trader.SelfCertification ? profile.Trader.SelfCertifiedAt : null;
            profile.Trader = trader;
            if (changed && profile.Status == SellerRegistrationStatus.Approved && o.Sellers.TraderTraceability && profile.TraderStatus == TraderStatus.Trader)
            {
                profile.Verification = null;
                await SubmitVerificationAsync(session, profile, ct);
            }
        }
        if (input.Dac7 is { } dac7)
        {
            dac7.RequestedAt = profile.Dac7.RequestedAt;
            dac7.RemindersSent = profile.Dac7.RemindersSent;
            dac7.LastReminderAt = profile.Dac7.LastReminderAt;
            profile.Dac7 = dac7;
            if (profile.PayoutsBlocked && dac7.Missing().Count == 0)
            {
                profile.PayoutsBlocked = false;
                await audit.RecordAsync(session, new CommerceAuditEntry
                {
                    Action = CommerceAuditActions.PayoutBlock, SubjectId = profile.Id!, OrganizationId = organizationId, Reason = "dac7-complete: payouts unblocked",
                }, ct);
            }
        }
        await session.SaveChangesAsync(ct);
        return profile;
    }

    /// <summary>
    /// Submits the registration: requires the data the mode needs, the current platform terms and DPA accepted
    /// (GB14, P2B Art. 3), then approves at once (<c>Open</c>, with a trader check through Approvals) or raises a
    /// registration request (<c>ApprovalRequired</c>). <c>Closed</c> lets only platform admins register sellers.
    /// </summary>
    public async Task<SellerProfile> SubmitAsync(string organizationId, IReadOnlyCollection<string> acceptedDocumentIds, CancellationToken ct)
    {
        await EnsureMayManageAsync(organizationId, ct);
        var o = options.Value;
        if (o.Sellers.Registration == SellerRegistrationMode.Closed && !await access.IsPlatformAdminAsync(ct))
            throw new SparkValidationException("Seller registration is closed on this platform.");

        using var session = store.OpenAsyncSession();
        var profile = await LoadOrCreateAsync(session, organizationId, ct);
        if (profile.Status is SellerRegistrationStatus.Pending or SellerRegistrationStatus.Approved)
            return profile;
        if (profile.Status == SellerRegistrationStatus.Suspended)
            throw new SparkValidationException("A suspended seller cannot register again.");

        var missing = MissingForSubmit(profile, o);
        if (missing.Count > 0)
            throw new SparkValidationException("The registration is incomplete: " + string.Join(", ", missing));

        // GB14 + P2B: the platform's terms for sellers and its DPA, in their current versions, must be accepted.
        var now = clock.GetUtcNow();
        var inForce = await legalDocuments.GetInForceAsync(null, now, ct);
        foreach (var kind in new[] { LegalDocumentKinds.PlatformTerms, LegalDocumentKinds.DataProcessingAgreement })
        {
            if (inForce.FirstOrDefault(d => d.Kind == kind) is not { } current)
                continue; // nothing published: startup warns, the registration is not blocked by an absent document
            if (!acceptedDocumentIds.Contains(current.Id))
                throw new SparkValidationException($"Accept the current {kind} (version {current.Version}) to register.");
            await legalDocuments.RecordAcceptanceAsync(session, organizationId, current, "seller-registration", ct);
            if (kind == LegalDocumentKinds.PlatformTerms) profile.AcceptedPlatformTermsId = current.Id;
            else profile.AcceptedDpaId = current.Id;
        }

        profile.SubmittedAt = now;
        if (o.Merchant == MerchantMode.ConnectedSellers && o.Sellers.Dac7.Enabled)
            profile.Dac7.RequestedAt ??= now;

        var adminRegisters = o.Sellers.Registration == SellerRegistrationMode.Closed;
        if (o.Sellers.Registration == SellerRegistrationMode.ApprovalRequired)
        {
            profile.Status = SellerRegistrationStatus.Pending;
            var submitted = await approvals.SubmitAsync<SellerRegistrationApproval>(session, new ApprovalSubmission
            {
                Subject = Subject(profile),
                Title = $"Seller registration: {profile.Trader.Name ?? organizationId}",
                Summary = $"{profile.TraderStatus}; {profile.Trader.Address}",
                RequestedBy = access.UserId,
                OrganizationId = organizationId,
                ReplacePending = true,
            }, ct);
            profile.RegistrationRequestId = submitted.RequestId;
        }
        else
        {
            profile.Status = SellerRegistrationStatus.Approved;
            profile.ApprovedAt = now;
            if (adminRegisters)
                profile.Verification = new TraderVerification { Method = "manual", Passed = true, By = access.UserId, At = now, Notes = "Registered by a platform admin." };
            else if (o.Sellers.TraderTraceability && profile.TraderStatus == TraderStatus.Trader)
                await SubmitVerificationAsync(session, profile, ct);
        }
        await session.SaveChangesAsync(ct);
        logger.LogInformation("Seller {OrganizationId} registration submitted: {Status}", organizationId, profile.Status);
        return profile;
    }

    /// <summary>Stores the identity document (DSA Art. 30(1)(b)) as an attachment. Never served to the seller's staff
    /// again; only <see cref="CommerceRights.ViewTraderData"/> holders download it.</summary>
    public async Task UploadIdentityDocumentAsync(string organizationId, Stream content, string contentType, CancellationToken ct)
    {
        await EnsureMayManageAsync(organizationId, ct);
        using var session = store.OpenAsyncSession();
        var profile = await LoadOrCreateAsync(session, organizationId, ct);
        session.Advanced.Attachments.Store(profile, SellerProfile.IdentityDocumentAttachment, content, contentType);
        profile.Trader.HasIdentityDocument = true;
        await session.SaveChangesAsync(ct);
    }

    /// <summary>Downloads the identity document for a holder of <see cref="CommerceRights.ViewTraderData"/>, audited.
    /// Null when refused or absent (the caller answers with the standard refusal).</summary>
    public async Task<(byte[] Content, string ContentType)?> DownloadIdentityDocumentAsync(string organizationId, CancellationToken ct)
    {
        if (!await access.MayViewTraderDataAsync(ct)) return null;
        using var session = store.OpenAsyncSession();
        var profileId = SellerProfile.IdFor(organizationId);
        using var attachment = await session.Advanced.Attachments.GetAsync(profileId, SellerProfile.IdentityDocumentAttachment, ct);
        if (attachment is null) return null;
        using var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer, ct);
        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = CommerceAuditActions.TraderDataViewed, SubjectId = profileId, OrganizationId = organizationId, Reason = "identity-document",
        }, ct);
        await session.SaveChangesAsync(ct);
        return (buffer.ToArray(), attachment.Details.ContentType);
    }

    /// <summary>Files a P2B complaint (Art. 11) as a decision request with a deadline.</summary>
    public async Task<string> ComplainAsync(string organizationId, string subject, string details, CancellationToken ct)
    {
        await EnsureMayManageAsync(organizationId, ct);
        if (!options.Value.Sellers.ComplaintHandling)
            throw new SparkValidationException("Complaint handling is switched off on this platform.");
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(details);
        using var session = store.OpenAsyncSession();
        var result = await approvals.SubmitAsync<SellerComplaintApproval>(session, new ApprovalSubmission
        {
            Subject = new ApprovalSubject(nameof(SellerProfile), SellerProfile.IdFor(organizationId), Guid.NewGuid().ToString("N")),
            Title = subject.Length > 200 ? subject[..200] : subject,
            Summary = details.Length > 4000 ? details[..4000] : details,
            RequestedBy = access.UserId,
            OrganizationId = organizationId,
            DecideWithin = options.Value.Sellers.ComplaintDeadline,
        }, ct);
        await session.SaveChangesAsync(ct);
        return result.RequestId;
    }

    /// <summary>Suspends a seller (platform admin). The DSA statement of reasons is issued by the notice service.</summary>
    public async Task SuspendAsync(IAsyncDocumentSession session, string organizationId, string reason, CancellationToken ct)
    {
        if (!await access.IsPlatformAdminAsync(ct))
            throw new SparkValidationException("Only platform admins suspend sellers.");
        var profile = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(organizationId), ct)
            ?? throw new SparkValidationException("The organization is not a seller.");
        profile.Status = SellerRegistrationStatus.Suspended;
        profile.StatusReason = reason;
    }

    /// <summary>The fields still missing before submitting (translation keys).</summary>
    internal static IReadOnlyList<string> MissingForSubmit(SellerProfile profile, SparkCommerceOptions options)
    {
        var missing = new List<string>();
        if (profile.TraderStatus == TraderStatus.Undeclared) missing.Add("commerce.trader.status");
        if (profile.TraderStatus == TraderStatus.Trader && options.Sellers.TraderTraceability)
            missing.AddRange(profile.Trader.Missing());
        // DAC7 data may follow later (reminders, then the payout block); it never blocks registration.
        return missing;
    }

    private async Task SubmitVerificationAsync(IAsyncDocumentSession session, SellerProfile profile, CancellationToken ct)
        => await approvals.SubmitAsync<TraderVerificationApproval>(session, new ApprovalSubmission
        {
            Subject = Subject(profile),
            Title = $"Verify trader: {profile.Trader.Name ?? profile.OrganizationId}",
            Summary = $"Register: {profile.Trader.TradeRegister} {profile.Trader.RegistrationNumber}; VAT: {profile.Trader.VatId}",
            RequestedBy = access.UserId,
            OrganizationId = profile.OrganizationId,
            ReplacePending = true,
        }, ct);

    private static ApprovalSubject Subject(SellerProfile profile) => new(nameof(SellerProfile), profile.Id!);

    private static bool TraderChanged(TraderData a, TraderData b)
        => a.Name != b.Name || a.Address != b.Address || a.Country != b.Country || a.Phone != b.Phone || a.Email != b.Email
           || a.PaymentAccount != b.PaymentAccount || a.TradeRegister != b.TradeRegister || a.RegistrationNumber != b.RegistrationNumber || a.VatId != b.VatId;

    private async Task EnsureMayManageAsync(string organizationId, CancellationToken ct)
    {
        if (!await access.MayManageSellerAsync(organizationId, ct))
            throw new SparkRowLevelAccessDeniedException("Manage/SellerProfile");
    }

    private async Task<SellerProfile> LoadOrCreateAsync(IAsyncDocumentSession session, string organizationId, CancellationToken ct)
    {
        var id = SellerProfile.IdFor(organizationId);
        var profile = await session.LoadAsync<SellerProfile>(id, ct);
        if (profile is not null) return profile;
        profile = new SellerProfile { Id = id, OrganizationId = organizationId, CreatedAt = clock.GetUtcNow() };
        await session.StoreAsync(profile, id, ct);
        return profile;
    }
}
