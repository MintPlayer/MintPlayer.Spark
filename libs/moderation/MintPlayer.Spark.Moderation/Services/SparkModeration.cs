using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Moderation.Documents;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// The write paths. Every vote-side write — the vote, its ledger entries, the tally and the cap
/// counters — is one optimistic-concurrency transaction (spike S-MOD-E), retried on a fresh session
/// when a concurrent vote on the same target or by the same voter won the race.
/// </summary>
internal sealed partial class SparkModeration : ISparkModeration
{
    private const int MaxAttempts = 4;

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ModerationTargets targets;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ModerationUserState userState;
    [Inject] private readonly ReputationLedger ledger;
    [Inject] private readonly ModerationAudit audit;
    [Inject] private readonly IModerationAccounts accounts;
    [Inject] private readonly IOptions<SparkModerationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<SparkModeration> logger;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    // ---- votes ---------------------------------------------------------------------------------

    public async Task<ModerationVoteState> VoteAsync(Guid objectTypeId, string id, int direction, CancellationToken cancellationToken = default)
    {
        if (direction is < -1 or > 1)
            throw new SparkValidationException("A vote is +1, -1 or 0.", "direction");

        var voterId = RequireUser();
        // Before the right check: a suspension withdraws every earned privilege, so asking for the
        // right first answered a suspended voter with the no-right refusal (404) instead of saying
        // why. It depends on the caller only, never on the target, so it reveals nothing about it.
        var voter = await userState.GetCurrentAsync();
        if (voter?.IsSuspended == true)
            throw new SparkValidationException("Your account is suspended.");

        var target = await targets.ResolveAsync(objectTypeId, id);

        var right = direction switch { > 0 => ModerationRights.Vote, < 0 => ModerationRights.Downvote, _ => null };
        if (right is not null)
            await permissions.EnsureAuthorizedAsync(right, target.TypeName, cancellationToken);
        if (target.AuthorId is { } author && author == voterId)
            throw new SparkValidationException("You cannot vote on your own post.");
        if (await IsLockedAsync(target.Id, cancellationToken))
            throw new SparkValidationException("This post is locked.");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var state = await WriteVoteAsync(voterId, voter, target, direction, cancellationToken);
                var (canUp, canDown) = await VoteRightsAsync(target.TypeName, cancellationToken);
                return WithRights(state, canUp, canDown);
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                // A concurrent vote moved the tally or a counter; decide again on fresh state.
                logger.LogDebug("Vote on {TargetId} by {VoterId} lost a race (attempt {Attempt}); retrying.", target.Id, voterId, attempt);
            }
        }
    }

    private async Task<ModerationVoteState> WriteVoteAsync(string voterId, ModerationUserSnapshot? voter, ModerationTarget target, int direction, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        var day = ModerationIds.Day(now);
        var settings = options.Value;
        var fraud = settings.Fraud;
        var voteId = ModerationIds.Vote(voterId, target.Id);
        var authorId = target.AuthorId;

        using var session = OpenSession();
        var voteLazy = session.Advanced.Lazily.LoadAsync<ModerationVote>(voteId);
        var tallyLazy = session.Advanced.Lazily.LoadAsync<ModerationTally>(ModerationIds.Tally(target.Id));
        var castLazy = session.Advanced.Lazily.LoadAsync<ModerationCounter>(ModerationIds.CastCounter(voterId, day));
        Lazy<Task<ModerationCounter>>? receivedLazy = null;
        Lazy<Task<Dictionary<string, ModerationCounter>>>? pairLazy = null;
        var pairIds = new List<string>();
        if (authorId is not null)
        {
            receivedLazy = session.Advanced.Lazily.LoadAsync<ModerationCounter>(ModerationIds.RecipientCounter(authorId, day));
            for (var d = 0; d < 30; d++)
                pairIds.Add(ModerationIds.PairCounter(voterId, authorId, ModerationIds.Day(now.AddDays(-d))));
            pairLazy = session.Advanced.Lazily.LoadAsync<ModerationCounter>(pairIds);
        }
        await session.Advanced.Eagerly.ExecuteAllPendingLazyOperationsAsync(cancellationToken);

        var vote = await voteLazy.Value;
        var tally = await tallyLazy.Value ?? new ModerationTally { TargetId = target.Id };

        if (vote is not null && vote.Direction == direction)
            return State(target.Id, tally, direction, locked: false);

        // Fraud measure 4: votes cast per voter per day. Counted per cast or change, never refunded.
        var cast = await castLazy.Value ?? new ModerationCounter();
        if (direction != 0 && cast.Count >= fraud.MaxVotesCastPerDay)
            throw new SparkThrottledException($"You have cast {fraud.MaxVotesCastPerDay} votes today.", now.Date.AddDays(1) - now);

        // Withdraw what the previous direction wrote: a Retraction per entry, the tally drops it.
        if (vote is not null && vote.Direction != 0)
        {
            var previous = vote.EventIds.Count == 0 ? new Dictionary<string, ReputationEvent>() : await session.LoadAsync<ReputationEvent>(vote.EventIds, cancellationToken);
            foreach (var entry in previous.Values)
            {
                if (entry is not null)
                    await ReputationLedger.CompensateAsync(session, entry, ReputationEventKinds.Retraction, now);
            }
            if (vote.Direction > 0) tally.Up = Math.Max(0, tally.Up - 1);
            else tally.Down = Math.Max(0, tally.Down - 1);
        }

        var isNew = vote is null;
        vote ??= new ModerationVote { VoterId = voterId, TargetId = target.Id };
        vote.Revision++;
        vote.Direction = direction;
        vote.EventIds = [];
        vote.TargetType = target.TypeName;
        vote.AuthorId = authorId;
        vote.TargetPostedAtUtc = target.PostedAtUtc;
        vote.CastAtUtc = now;
        vote.Day = day;
        vote.DayNumber = ModerationIds.DayNumber(now);

        if (direction != 0)
        {
            if (direction > 0) tally.Up++;
            else tally.Down++;
            cast.Count++;
            await StoreCounterAsync(session, cast, ModerationIds.CastCounter(voterId, day), now);

            if (authorId is not null)
            {
                var received = await receivedLazy!.Value ?? new ModerationCounter();
                var pairs = await pairLazy!.Value;
                var pairToday = pairs.GetValueOrDefault(pairIds[0]) ?? new ModerationCounter();
                var pair30 = pairs.Values.Where(c => c is not null).Sum(c => c!.Count);

                var kind = direction > 0 ? ReputationEventKinds.UpvoteReceived : ReputationEventKinds.DownvoteReceived;
                var points = settings.PointsFor(kind);
                string? zeroedBy = null;

                // Fraud measure 3: a voter younger than 7 days or under 50 rep changes the score only.
                var voterAge = voter?.AgeDays ?? 0;
                var voterRep = voter?.Reputation ?? 0;
                if (voterAge < fraud.EligibleVoterMinAgeDays || voterRep < fraud.EligibleVoterMinReputation)
                    zeroedBy = "ineligible-voter";
                // Fraud measure 4: the voter→recipient pair is credited at most 3×/day and 10×/30 days.
                else if (pairToday.Count >= fraud.MaxCreditedPairVotesPerDay || pair30 >= fraud.MaxCreditedPairVotesPer30Days)
                    zeroedBy = "pair-cap";
                // Fraud measure 4: at most 200 rep a day per recipient from votes.
                else if (points > 0)
                {
                    var remaining = fraud.MaxReputationPerRecipientPerDay - received.Count;
                    if (remaining <= 0)
                        zeroedBy = "daily-cap";
                    else
                        points = Math.Min(points, remaining);
                }
                if (zeroedBy is not null)
                    points = 0;

                if (points > 0)
                {
                    received.Count += points;
                    await StoreCounterAsync(session, received, ModerationIds.RecipientCounter(authorId, day), now);
                }
                if (points != 0)
                {
                    pairToday.Count++;
                    await StoreCounterAsync(session, pairToday, pairIds[0], now);
                }

                var recipientEvent = NewVoteEvent(voteId, vote.Revision, "recipient", authorId, kind, points, voterId, target, now, day, fraud);
                recipientEvent.VoteWeight = direction > 0 && points > 0 ? 1 : 0;
                recipientEvent.ZeroedBy = zeroedBy;
                await session.StoreAsync(recipientEvent, string.Empty, recipientEvent.Id, cancellationToken);
                vote.EventIds.Add(recipientEvent.Id!);
            }

            if (direction < 0 && ChargesDownvoter(target.TypeName, settings))
            {
                var cost = settings.PointsFor(ReputationEventKinds.DownvoteCast);
                if (cost != 0)
                {
                    var voterEvent = NewVoteEvent(voteId, vote.Revision, "voter", voterId, ReputationEventKinds.DownvoteCast, cost, null, target, now, day, fraud);
                    await session.StoreAsync(voterEvent, string.Empty, voterEvent.Id, cancellationToken);
                    vote.EventIds.Add(voterEvent.Id!);
                }
            }
        }

        if (isNew)
            await session.StoreAsync(vote, string.Empty, voteId, cancellationToken);
        await session.StoreAsync(tally, ModerationIds.Tally(target.Id), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return State(target.Id, tally, direction, locked: false);
    }

    private static ReputationEvent NewVoteEvent(string voteId, int revision, string role, string userId, string kind, int points, string? voterId, ModerationTarget target, DateTime now, string day, ModerationFraudOptions fraud) => new()
    {
        Id = ModerationIds.VoteEvent(voteId, revision, role),
        UserId = userId,
        Kind = kind,
        Points = points,
        VoterId = voterId,
        VoteId = voteId,
        TargetId = target.Id,
        TargetType = target.TypeName,
        CreatedAtUtc = now,
        Day = day,
        // Fraud measure 5: nothing a vote earns counts until the crediting job has passed this.
        CreditableAfterUtc = now.AddHours(fraud.CreditDelayHours),
        Credited = false,
    };

    private static bool ChargesDownvoter(string typeName, SparkModerationOptions settings)
        => settings.DownvoteCastTypes.Count == 0 || settings.DownvoteCastTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase);

    private static async Task StoreCounterAsync(IAsyncDocumentSession session, ModerationCounter counter, string id, DateTime now)
    {
        await session.StoreAsync(counter, id);
        session.Advanced.GetMetadataFor(counter)["@expires"] = now.Date.AddDays(31).ToString("O");
    }

    public async Task<IReadOnlyList<ModerationVoteState>> GetVotesAsync(Guid objectTypeId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count > 100)
            throw new SparkValidationException("At most 100 ids per request.");

        var visible = new List<ModerationTarget>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (await targets.TryResolveAsync(objectTypeId, id) is { } target)
                visible.Add(target);
        }
        if (visible.Count == 0)
            return [];

        var voterId = currentUser.IsAuthenticated ? currentUser.Id : null;
        // One type per request (objectTypeId), so the caller's rights are asked once, not per id.
        var (canUp, canDown) = await VoteRightsAsync(visible[0].TypeName, cancellationToken);
        using var session = documentStore.OpenAsyncSession();
        var tallies = await session.LoadAsync<ModerationTally>(visible.Select(t => ModerationIds.Tally(t.Id)), cancellationToken);
        var locks = await session.LoadAsync<ModerationLock>(visible.Select(t => ModerationIds.Lock(t.Id)), cancellationToken);
        var votes = voterId is null
            ? new Dictionary<string, ModerationVote>()
            : await session.LoadAsync<ModerationVote>(visible.Select(t => ModerationIds.Vote(voterId, t.Id)), cancellationToken);

        return visible.Select(t => WithRights(State(
            t.Id,
            tallies.GetValueOrDefault(ModerationIds.Tally(t.Id)) ?? new ModerationTally { TargetId = t.Id },
            voterId is null ? 0 : votes.GetValueOrDefault(ModerationIds.Vote(voterId, t.Id))?.Direction ?? 0,
            locks.GetValueOrDefault(ModerationIds.Lock(t.Id)) is not null), canUp, canDown)).ToList();
    }

    private static ModerationVoteState State(string id, ModerationTally tally, int mine, bool locked)
        => new() { Id = id, Up = tally.Up, Down = tally.Down, Score = tally.Score, MyVote = mine, Locked = locked };

    private static ModerationVoteState WithRights(ModerationVoteState state, bool canUpvote, bool canDownvote)
        => new()
        {
            Id = state.Id, Up = state.Up, Down = state.Down, Score = state.Score, MyVote = state.MyVote, Locked = state.Locked,
            CanUpvote = canUpvote, CanDownvote = canDownvote,
        };

    /// <summary>
    /// Whether the caller holds <c>Vote</c> / <c>Downvote</c> on <paramref name="typeName"/> — what the
    /// widget needs to disable an arrow it would otherwise offer and have refused. Anonymous: neither.
    /// </summary>
    private async Task<(bool Up, bool Down)> VoteRightsAsync(string typeName, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
            return (false, false);
        return (await permissions.IsAllowedAsync(ModerationRights.Vote, typeName, cancellationToken),
                await permissions.IsAllowedAsync(ModerationRights.Downvote, typeName, cancellationToken));
    }

    // ---- flags ---------------------------------------------------------------------------------

    public async Task FlagAsync(Guid objectTypeId, string id, string reason, CancellationToken cancellationToken = default)
    {
        var flaggerId = RequireUser();
        if (string.IsNullOrWhiteSpace(reason))
            throw new SparkValidationException("Say why you flag this.", "reason");
        // Before the right check, as for a vote: a suspension withdraws the Flag privilege too.
        if (await userState.IsCurrentUserSuspendedAsync())
            throw new SparkValidationException("Your account is suspended.");
        var target = await targets.ResolveAsync(objectTypeId, id);
        await permissions.EnsureAuthorizedAsync(ModerationRights.Flag, target.TypeName, cancellationToken);

        var now = UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var session = OpenSession();
                var flagId = ModerationIds.Flag(flaggerId, target.Id);
                var caseId = ModerationIds.FlagCase(target.Id);
                var flag = await session.LoadAsync<ModerationFlag>(flagId, cancellationToken);
                var reviewCase = await session.LoadAsync<ReviewCase>(caseId, cancellationToken);
                if (flag is not null && flag.Status == ModerationCaseStatus.Open)
                    return; // One open flag per user and target; answered like a success (no oracle).

                if (reviewCase is null)
                {
                    reviewCase = new ReviewCase { Kind = "flag", TargetId = target.Id, TargetType = target.TypeName, OpenedAtUtc = now };
                    if (target.AuthorId is { } author)
                        reviewCase.AccountIds.Add(author);
                    await session.StoreAsync(reviewCase, string.Empty, caseId, cancellationToken);
                }
                else if (reviewCase.Status != ModerationCaseStatus.Open)
                {
                    // A new flag after a decision reopens the case.
                    reviewCase.Status = ModerationCaseStatus.Open;
                    reviewCase.Decision = null;
                    reviewCase.DecidedAtUtc = null;
                    reviewCase.DecidedBy = null;
                    reviewCase.FlagCount = 0;
                    reviewCase.OpenedAtUtc = now;
                }
                reviewCase.FlagCount++;
                reviewCase.Summary = reason.Length > 500 ? reason[..500] : reason;

                if (flag is null)
                {
                    flag = new ModerationFlag { FlaggerId = flaggerId, TargetId = target.Id, TargetType = target.TypeName };
                    await session.StoreAsync(flag, string.Empty, flagId, cancellationToken);
                }
                flag.Reason = reviewCase.Summary;
                flag.RaisedAtUtc = now;
                flag.CaseId = caseId;
                flag.Status = ModerationCaseStatus.Open;
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    // ---- locks ---------------------------------------------------------------------------------

    public async Task LockAsync(Guid objectTypeId, string id, string? reason, CancellationToken cancellationToken = default)
    {
        var actor = RequireUser();
        var target = await targets.ResolveAsync(objectTypeId, id);
        await permissions.EnsureAuthorizedAsync(ModerationRights.Lock, target.TypeName, cancellationToken);

        using var session = documentStore.OpenAsyncSession();
        await session.StoreAsync(new ModerationLock
        {
            TargetId = target.Id,
            TargetType = target.TypeName,
            Reason = reason,
            LockedBy = actor,
            LockedAtUtc = UtcNow,
        }, ModerationIds.Lock(target.Id), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("lock", target.Id, target.TypeName, target.AuthorId, reason: reason, cancellationToken: cancellationToken);
    }

    public async Task UnlockAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default)
    {
        RequireUser();
        var target = await targets.ResolveAsync(objectTypeId, id);
        await permissions.EnsureAuthorizedAsync(ModerationRights.Lock, target.TypeName, cancellationToken);

        using var session = documentStore.OpenAsyncSession();
        session.Delete(ModerationIds.Lock(target.Id));
        await session.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("unlock", target.Id, target.TypeName, target.AuthorId, cancellationToken: cancellationToken);
    }

    internal async Task<bool> IsLockedAsync(string targetId, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.Advanced.ExistsAsync(ModerationIds.Lock(targetId), cancellationToken);
    }

    // ---- accounts ------------------------------------------------------------------------------

    public async Task SuspendAsync(string userId, int? days, string? reason, string? caseId = null, CancellationToken cancellationToken = default)
    {
        var actor = RequireUser();
        await permissions.EnsureAuthorizedAsync(ModerationRights.Suspend, ModerationRights.Target, cancellationToken);
        if (string.IsNullOrEmpty(userId))
            throw new SparkValidationException("Name the account.", "userId");
        if (userId == actor)
            throw new SparkValidationException("You cannot suspend yourself.");
        if (days is <= 0)
            throw new SparkValidationException("Days must be positive (omit it to suspend until lifted).", "days");

        var now = UtcNow;
        DateTime? until = days is { } d ? now.AddDays(d) : null;

        // The document first: it is what the write block and the privilege provider read, by id, on
        // the very next request. The Identity lockout + stamp refresh then ends the sign-in.
        using (var session = documentStore.OpenAsyncSession())
        {
            await session.StoreAsync(new ModerationSuspension
            {
                UserId = userId,
                UntilUtc = until,
                Reason = reason,
                SuspendedBy = actor,
                SuspendedAtUtc = now,
                CaseId = caseId,
            }, ModerationIds.Suspension(userId), cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        await accounts.LockOutAsync(userId, until is { } u ? new DateTimeOffset(u, TimeSpan.Zero) : null, cancellationToken);
        await audit.WriteAsync("suspend", null, null, userId, caseId, reason, until is null ? "until lifted" : $"until {until:O}", cancellationToken);
    }

    public async Task UnsuspendAsync(string userId, CancellationToken cancellationToken = default)
    {
        RequireUser();
        await permissions.EnsureAuthorizedAsync(ModerationRights.Suspend, ModerationRights.Target, cancellationToken);
        using (var session = documentStore.OpenAsyncSession())
        {
            session.Delete(ModerationIds.Suspension(userId));
            await session.SaveChangesAsync(cancellationToken);
        }
        await accounts.LiftLockOutAsync(userId, cancellationToken);
        await audit.WriteAsync("unsuspend", null, null, userId, cancellationToken: cancellationToken);
    }

    public async Task MergeAccountsAsync(string duplicateId, string intoId, string? caseId = null, CancellationToken cancellationToken = default)
    {
        RequireUser();
        await permissions.EnsureAuthorizedAsync(ModerationRights.Suspend, ModerationRights.Target, cancellationToken);
        if (string.IsNullOrEmpty(duplicateId) || string.IsNullOrEmpty(intoId) || duplicateId == intoId)
            throw new SparkValidationException("Name two different accounts.");

        var voteIds = await VotesCastByAsync(documentStore, duplicateId, cancellationToken);
        await ledger.ReverseVotesAsync(voteIds, "merge", caseId, cancellationToken);
        await audit.WriteAsync("merge", null, null, duplicateId, caseId, details: $"into {intoId}; {voteIds.Count} vote(s) reversed", cancellationToken: cancellationToken);
    }

    internal static async Task<IReadOnlyList<string>> VotesCastByAsync(IDocumentStore store, string voterId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var rows = await ModerationQueries.StreamAllAsync(session, () => session.Query<ModerationVote>().Where(v => v.VoterId == voterId), cancellationToken);
        return rows.Select(r => r.Id).ToList();
    }

    public async Task<ModerationReputation> GetReputationAsync(string userId, CancellationToken cancellationToken = default)
    {
        // The caller's own read goes through the request snapshot, which also records today's activity.
        var snapshot = currentUser.IsAuthenticated && currentUser.Id == userId
            ? (await userState.GetCurrentAsync())!
            : await userState.ReadAsync(userId);
        return new ModerationReputation
        {
            UserId = userId,
            Total = snapshot.Summary?.Total ?? 0,
            // Read live: a vote does not recompute its recipient's summary (see ReadPendingAsync).
            Pending = await ledger.ReadPendingAsync(userId, cancellationToken),
            Privileges = ModerationPrivilegeProvider.Earned(snapshot, options.Value).Select(p => p.Key).ToList(),
            Suspended = snapshot.IsSuspended,
        };
    }

    // ---- review --------------------------------------------------------------------------------

    public async Task DecideAsync(string caseId, string decision, string? accountId = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        var actor = RequireUser();
        await permissions.EnsureAuthorizedAsync(ModerationRights.Review, ModerationRights.Target, cancellationToken);
        if (string.IsNullOrEmpty(caseId) || !caseId.StartsWith("ModerationCases/", StringComparison.Ordinal))
            throw new SparkRowLevelAccessDeniedException("Review/Moderation");

        ReviewCase? reviewCase;
        using (var read = documentStore.OpenAsyncSession())
            reviewCase = await read.LoadAsync<ReviewCase>(caseId, cancellationToken);
        if (reviewCase is null)
            throw new SparkRowLevelAccessDeniedException("Review/Moderation");
        if (reviewCase.Status != ModerationCaseStatus.Open)
            throw new SparkValidationException($"This case was already decided ({reviewCase.Status}).");

        decision = decision?.Trim().ToLowerInvariant() ?? string.Empty;
        var now = UtcNow;
        string status;
        if (reviewCase.Kind == "flag")
        {
            status = decision switch
            {
                "uphold" => ModerationCaseStatus.Upheld,
                "decline" => ModerationCaseStatus.Declined,
                _ => throw new SparkValidationException("A flag case is upheld or declined.", "decision"),
            };
            await SettleFlagsAsync(reviewCase, caseId, status, now, cancellationToken);
        }
        else
        {
            switch (decision)
            {
                case "dismiss":
                    status = ModerationCaseStatus.Dismissed;
                    break;
                case "reverse":
                    status = ModerationCaseStatus.Reversed;
                    await ledger.ReverseVotesAsync(reviewCase.VoteIds, "moderator", caseId, cancellationToken);
                    break;
                case "merge":
                    status = ModerationCaseStatus.Merged;
                    var into = reviewCase.AccountIds.FirstOrDefault(a => a != accountId);
                    if (accountId is null || into is null || !reviewCase.AccountIds.Contains(accountId))
                        throw new SparkValidationException("Name the duplicate account (one of the case's accounts).", "accountId");
                    await MergeAccountsAsync(accountId, into, caseId, cancellationToken);
                    break;
                case "suspend":
                    status = ModerationCaseStatus.Suspended;
                    if (accountId is null || !reviewCase.AccountIds.Contains(accountId))
                        throw new SparkValidationException("Name the account to suspend (one of the case's accounts).", "accountId");
                    await SuspendAsync(accountId, null, reason ?? "Escalated from review", caseId, cancellationToken);
                    break;
                default:
                    throw new SparkValidationException("A fraud case is dismissed, reversed, merged or escalated to a suspension.", "decision");
            }
        }

        using (var write = documentStore.OpenAsyncSession())
        {
            write.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var stored = await write.LoadAsync<ReviewCase>(caseId, cancellationToken);
            stored!.Status = status;
            stored.Decision = decision;
            stored.DecidedAtUtc = now;
            stored.DecidedBy = actor;
            await write.SaveChangesAsync(cancellationToken);
        }
        await audit.WriteAsync("decide:" + decision, reviewCase.TargetId, reviewCase.TargetType, accountId, caseId, reason, reviewCase.Kind, cancellationToken);
    }

    private async Task SettleFlagsAsync(ReviewCase reviewCase, string caseId, string status, DateTime now, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        var flags = await session.Query<ModerationFlag>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(f => f.CaseId == caseId && f.Status == ModerationCaseStatus.Open)
            .Take(1024)
            .ToListAsync(cancellationToken);
        var kind = status == ModerationCaseStatus.Upheld ? ReputationEventKinds.FlagUpheld : ReputationEventKinds.FlagDeclined;
        var points = options.Value.PointsFor(kind);
        var users = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in flags)
        {
            flag.Status = status;
            if (points == 0)
                continue;
            var eventId = ModerationIds.FlagEvent(flag.Id!, $"{status}-{now:yyyyMMddHHmmss}");
            // Flag outcomes are decided by a moderator, not farmed: credited at once.
            await session.StoreAsync(new ReputationEvent
            {
                UserId = flag.FlaggerId,
                Kind = kind,
                Points = points,
                TargetId = reviewCase.TargetId,
                TargetType = reviewCase.TargetType,
                CreatedAtUtc = now,
                Day = ModerationIds.Day(now),
                CreditableAfterUtc = now,
                Credited = true,
                CaseId = caseId,
            }, string.Empty, eventId, cancellationToken);
            users.Add(flag.FlaggerId);
        }
        await session.SaveChangesAsync(cancellationToken);
        await ledger.RecomputeAsync(users, cancellationToken);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private string RequireUser()
        => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id
            ? id
            : throw new SparkAccessDeniedException("Moderation");

    private IAsyncDocumentSession OpenSession()
    {
        var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        return session;
    }
}
