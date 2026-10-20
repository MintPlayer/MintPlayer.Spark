using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Moderation;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Services;

/// <summary>What a reporter submits (DSA Art. 16(2)).</summary>
public sealed record DsaNoticeInput(
    string TargetKind,
    string TargetId,
    string Reason,
    string Explanation,
    string? Location,
    string? ContactName,
    string? ContactEmail,
    bool GoodFaith);

/// <summary>A reviewer's decision on a notice (Art. 17).</summary>
public sealed record DsaDecisionInput(
    string NoticeId,
    bool TakeAction,
    DsaRestriction? Restriction,
    DateTimeOffset? RestrictionEndsAt,
    IReadOnlyList<string>? Territories,
    string? Facts,
    DsaGround? Ground,
    string? GroundExplanation,
    bool Automated = false);

/// <summary>
/// Notice and action (GA5, Regulation (EU) 2022/2065 Art. 16 and 17), through Moderation: anyone files a notice; a
/// signed-in reporter's notice on a moderatable target is also flagged in Moderation's queue; Moderation's reviewers
/// (<c>Review/Moderation</c>) or holders of <see cref="CommerceRights.DecideNotices"/> decide; an action issues a
/// statement of reasons to the seller. Notices and statements feed the transparency export (Art. 15, 24).
/// </summary>
/// <remarks>Moderation offers no anonymous flag path and no decision hook (2026-10-19), so the notice record and the
/// decision live here; the Moderation flag is the reviewers' queue entry.</remarks>
internal sealed class DsaNotices(
    IDocumentStore store,
    IAccessControl accessControl,
    ISparkVisitor visitor,
    IServiceProvider services,
    TimeProvider clock,
    ILogger<DsaNotices> logger)
{
    private const string ModerationReview = "Review/Moderation";

    public async Task<string> SubmitAsync(DsaNoticeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.GoodFaith)
            throw new SparkValidationException("Confirm that the notice is accurate and complete, in good faith (DSA Art. 16(2)(d)).", nameof(input.GoodFaith));
        if (string.IsNullOrWhiteSpace(input.Explanation) || input.Explanation.Length > 10_000)
            throw new SparkValidationException("Explain why the content is illegal (at most 10000 characters).", nameof(input.Explanation));
        if (!DsaReasons.All.Contains(input.Reason))
            throw new SparkValidationException("Choose a reason.", nameof(input.Reason));

        var resolver = services.GetServices<IDsaNoticeTargetResolver>().FirstOrDefault(r => r.Kinds.Contains(input.TargetKind, StringComparer.Ordinal));
        var target = resolver is null ? null : await resolver.ResolveAsync(input.TargetKind, input.TargetId, ct);
        if (target is null)
            throw new SparkValidationException("The reported content was not found.", nameof(input.TargetId));

        // Visitors file notices too (Art. 16(1): "any individual or entity"); the visitor cookie keys the reporter.
        if (visitor.UserId is null && !visitor.HasVisitorCookie)
            await visitor.EnsureAsync();

        var notice = new DsaNotice
        {
            Id = "DsaNotices/" + Guid.NewGuid().ToString("N"),
            TargetKind = input.TargetKind,
            TargetId = target.TargetId,
            OrganizationId = target.OrganizationId,
            Reason = input.Reason,
            Explanation = input.Explanation.Trim(),
            Location = Trim(input.Location, 2048),
            ContactName = Trim(input.ContactName, 200),
            ContactEmail = Trim(input.ContactEmail, 320),
            GoodFaith = true,
            ReporterKey = visitor.IdentityKey,
            ReporterUserId = visitor.UserId,
            ReceivedAt = clock.GetUtcNow(),
        };
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(notice, notice.Id, ct);
            await session.SaveChangesAsync(ct);
        }

        if (visitor.UserId is not null && target.ModerationObjectTypeId is { } typeId)
        {
            try
            {
                if (services.GetService<ISparkModeration>() is { } moderation)
                    await moderation.FlagAsync(typeId, target.TargetId, $"dsa:{input.Reason} {notice.Id}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The notice is recorded either way; the flag is the reviewers' convenience (and refuses suspended users).
                logger.LogWarning(ex, "DSA notice {NoticeId} could not be flagged in Moderation", notice.Id);
            }
        }
        return notice.Id!;
    }

    public async Task<bool> MayDecideAsync(CancellationToken ct)
        => await accessControl.IsAllowedAsync(CommerceRights.DecideNotices, ct) || await accessControl.IsAllowedAsync(ModerationReview, ct);

    /// <summary>Decides a notice; on action, issues the statement of reasons (Art. 17(3)) in the same commit.</summary>
    public async Task<DsaNotice?> DecideAsync(DsaDecisionInput input, string? deciderId, CancellationToken ct)
    {
        if (!await MayDecideAsync(ct)) return null;
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var notice = await session.LoadAsync<DsaNotice>(input.NoticeId, ct);
        if (notice is null) return null;
        if (notice.Status != DsaNoticeStatus.Open)
            throw new SparkValidationException("This notice was already decided.");

        var now = clock.GetUtcNow();
        notice.DecidedAt = now;
        notice.DecidedBy = deciderId;
        notice.AutomatedDecision = input.Automated;
        if (input.TakeAction)
        {
            if (input.Restriction is null || input.Ground is null || string.IsNullOrWhiteSpace(input.Facts) || string.IsNullOrWhiteSpace(input.GroundExplanation))
                throw new SparkValidationException("A restriction needs its kind, the facts, and the legal or contractual ground (DSA Art. 17(3)).");
            var statement = BuildStatement(notice, input, deciderId, now);
            await session.StoreAsync(statement, statement.Id, ct);
            notice.Status = DsaNoticeStatus.ActionTaken;
            notice.StatementId = statement.Id;
        }
        else
        {
            notice.Status = DsaNoticeStatus.NoAction;
        }
        await session.SaveChangesAsync(ct);
        return notice;
    }

    internal static DsaStatementOfReasons BuildStatement(DsaNotice notice, DsaDecisionInput input, string? deciderId, DateTimeOffset now) => new()
    {
        Id = "DsaStatements/" + Guid.NewGuid().ToString("N"),
        NoticeId = notice.Id,
        OrganizationId = notice.OrganizationId,
        TargetKind = notice.TargetKind,
        TargetId = notice.TargetId,
        Restriction = input.Restriction!.Value,
        RestrictionEndsAt = input.RestrictionEndsAt,
        Territories = input.Territories?.ToList() ?? [],
        Facts = input.Facts!.Trim(),
        BasedOnNotice = true,
        Automated = input.Automated,
        Ground = input.Ground!.Value,
        GroundExplanation = input.GroundExplanation!.Trim(),
        IssuedAt = now,
        IssuedBy = deciderId,
    };

    /// <summary>The transparency figures for [from, to) (Art. 15(1)(b): notices by category, actions taken, median
    /// time to decision; Art. 24). Null when the caller may not see them.</summary>
    public async Task<Contracts.CommerceDsaTransparencyReport?> ReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!await MayDecideAsync(ct)) return null;
        using var session = store.OpenAsyncSession();
        var notices = new List<DsaNotice>();
        await using (var stream = await session.Advanced.StreamAsync(session.Query<DsaNotice>().Where(n => n.ReceivedAt >= from && n.ReceivedAt < to), ct))
            while (await stream.MoveNextAsync())
                notices.Add(stream.Current.Document);
        var statements = new List<DsaStatementOfReasons>();
        await using (var stream = await session.Advanced.StreamAsync(session.Query<DsaStatementOfReasons>().Where(s => s.IssuedAt >= from && s.IssuedAt < to), ct))
            while (await stream.MoveNextAsync())
                statements.Add(stream.Current.Document);
        return Report(from, to, notices, statements);
    }

    /// <summary>Pure core of <see cref="ReportAsync"/>.</summary>
    internal static Contracts.CommerceDsaTransparencyReport Report(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<DsaNotice> notices, IReadOnlyList<DsaStatementOfReasons> statements)
    {
        var durations = notices.Where(n => n.DecidedAt is not null).Select(n => (n.DecidedAt!.Value - n.ReceivedAt).TotalHours).Order().ToArray();
        double? median = durations.Length == 0 ? null
            : durations.Length % 2 == 1 ? durations[durations.Length / 2]
            : (durations[durations.Length / 2 - 1] + durations[durations.Length / 2]) / 2;
        return new Contracts.CommerceDsaTransparencyReport(
            from, to,
            notices.Count,
            notices.GroupBy(n => n.Reason).ToDictionary(g => g.Key, g => g.Count()),
            notices.Count(n => n.Status == DsaNoticeStatus.ActionTaken),
            notices.Count(n => n.Status == DsaNoticeStatus.NoAction),
            notices.Count(n => n.Status == DsaNoticeStatus.Open),
            notices.Count(n => n.AutomatedDecision),
            median,
            statements.GroupBy(s => s.Restriction.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            statements.Count(s => !s.BasedOnNotice));
    }

    private static string? Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();
}
