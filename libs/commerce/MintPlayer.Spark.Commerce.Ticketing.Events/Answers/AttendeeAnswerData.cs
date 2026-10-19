using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Answers;

/// <summary>
/// GB3 retention: erases attendee answers past their <see cref="AttendeeAnswerSet.EraseAfter"/> (the event's last date
/// plus the seller's retention, default 30 days). GDPR Art. 5(1)(e) storage limitation.
/// </summary>
internal sealed class AttendeeAnswerRetentionJob(IDocumentStore store, TimeProvider clock, ILogger<AttendeeAnswerRetentionJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 0 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var erased = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var session = store.OpenAsyncSession();
            var due = await session.Query<AttendeeAnswerSet>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(s => s.EraseAfter < now)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
                break;
            foreach (var set in due)
                session.Delete(set);
            await session.SaveChangesAsync(cancellationToken);
            erased += due.Count;
            if (due.Count < 500)
                break;
        }
        if (erased > 0)
            logger.LogInformation("Erased {Count} attendee answer sets past their retention (GB3).", erased);
    }
}

/// <summary>
/// The seller's export of attendee answers (GB14, GB3), kind <c>attendee-answers</c>: one row per answer of the seller's
/// own events. Answers to sensitive questions are left out unless the question sets <c>IncludeInExport</c> (§4.11).
/// Commerce checks the caller's role in the seller and audits the export.
/// </summary>
internal sealed class AttendeeAnswerExport(IDocumentStore store) : ISellerExportSource
{
    public string Kind => "attendee-answers";

    public IReadOnlyList<string> Columns { get; } = ["event", "dates", "order", "line", "position", "ticket_type", "question", "answer"];

    public async IAsyncEnumerable<IReadOnlyList<string?>> GetRowsAsync(string organizationId, DateTimeOffset? from, DateTimeOffset? to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var types = new Dictionary<string, TicketType?>(StringComparer.Ordinal);
        var skip = 0;
        while (true)
        {
            List<AttendeeAnswerSet> page;
            using (var session = store.OpenAsyncSession())
            {
                var query = session.Query<AttendeeAnswerSet>().Customize(c => c.WaitForNonStaleResults()).Where(s => s.OrganizationId == organizationId);
                if (from is { } start)
                    query = query.Where(s => s.UpdatedAt >= start);
                if (to is { } end)
                    query = query.Where(s => s.UpdatedAt < end);
                page = await query.OrderBy(s => s.Id).Skip(skip).Take(256).ToListAsync(cancellationToken);
                foreach (var typeId in page.Select(s => s.TicketTypeId).Distinct().Where(id => !types.ContainsKey(id)))
                    types[typeId] = await session.LoadAsync<TicketType>(typeId, cancellationToken);
            }
            foreach (var set in page)
            {
                var type = types[set.TicketTypeId];
                foreach (var answer in set.Answers)
                {
                    var question = type?.Questions.FirstOrDefault(q => q.Key == answer.QuestionKey);
                    if ((answer.Sensitive || question?.Sensitive == true) && question?.IncludeInExport != true)
                        continue;
                    yield return [set.EventId, string.Join(" ", set.DateIds), set.OrderNumber, set.LineId,
                        set.Position.ToString(System.Globalization.CultureInfo.InvariantCulture), set.TicketTypeId, answer.QuestionKey, answer.Value];
                }
            }
            if (page.Count < 256)
                yield break;
            skip += page.Count;
        }
    }
}

/// <summary>The buyer's attendee answers in their personal-data export (GDPR Art. 15, Art. 20).</summary>
internal sealed class AttendeeAnswerPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser> where TUser : SparkUser
{
    public string Name => "ticketing-attendee-answers";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        using var session = store.OpenAsyncSession();
        var sets = await session.Query<AttendeeAnswerSet>().Where(s => s.BuyerUserId == userId).Take(4096).ToListAsync(cancellationToken);
        return sets.Count == 0 ? null : sets.Select(s => new
        {
            s.OrderNumber,
            s.EventId,
            s.DateIds,
            s.Position,
            s.TicketTypeId,
            Answers = s.Answers.Select(a => new { a.QuestionKey, a.Value, a.ConsentGivenAt }),
            s.EraseAfter,
        }).ToList();
    }
}

/// <summary>Account deletion (GDPR Art. 17): the buyer's attendee answers are erased; they are not needed as proof of the contract.</summary>
internal sealed class AttendeeAnswerAccountDeletion<TUser>(IDocumentStore store) : ISparkAccountDeletionHandler<TUser> where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        while (true)
        {
            using var session = store.OpenAsyncSession();
            var sets = await session.Query<AttendeeAnswerSet>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(s => s.BuyerUserId == userId)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (sets.Count == 0)
                return;
            foreach (var set in sets)
                session.Delete(set);
            await session.SaveChangesAsync(cancellationToken);
            if (sets.Count < 500)
                return;
        }
    }
}
