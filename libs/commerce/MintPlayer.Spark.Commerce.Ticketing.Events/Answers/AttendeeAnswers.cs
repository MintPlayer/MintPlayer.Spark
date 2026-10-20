using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Answers;

/// <summary>
/// Attendee answers (GB3, PRD §4.11): the questions of an order's tickets and the answers per ticket position. The buyer
/// answers on the order page right after placing it (questions asked "at checkout") or later on the ticket page, until
/// the deadline before the date. Answers are erased after the event's retention, on the buyer's account deletion, when
/// the order never completes, and on a transfer or resale (Tickets calls <see cref="EraseAsync"/>).
/// </summary>
public interface IAttendeeAnswers
{
    /// <summary>The questions and answers of the caller's own order <paramref name="orderNumber"/>, or null when it is not theirs.</summary>
    Task<SparkAttendeeQuestionnaire?> GetAsync(string orderNumber, CancellationToken cancellationToken = default);

    /// <summary>Stores the answers of one ticket position of the caller's own order. Throws <see cref="AttendeeAnswerException"/> on invalid answers or a closed deadline.</summary>
    Task<SparkAttendeeQuestionnaire?> SaveAsync(SparkAttendeeAnswersRequest request, CancellationToken cancellationToken = default);

    /// <summary>Erases the answers of a ticket position (a transfer or resale, §4.11), or of a whole line when <paramref name="position"/> is null. Idempotent.</summary>
    Task EraseAsync(string orderId, string lineId, int? position = null, CancellationToken cancellationToken = default);

    /// <summary>Erases every answer of an order. Idempotent.</summary>
    Task EraseOrderAsync(string orderId, CancellationToken cancellationToken = default);
}

/// <summary>Refused answers: the per-question errors (translation keys under <c>ticketingEvents.answers.</c>).</summary>
public sealed class AttendeeAnswerException(IReadOnlyList<AttendeeAnswerError> errors, string message = "The answers were refused.") : Exception(message)
{
    public IReadOnlyList<AttendeeAnswerError> Errors { get; } = errors;

    public static AttendeeAnswerException Closed() => new([new AttendeeAnswerError("", "closed")], "The answers are closed.");

    public static AttendeeAnswerException NotFound() => new([new AttendeeAnswerError("", "notFound")], "No such ticket.");
}

internal sealed class AttendeeAnswers(
    IDocumentStore store,
    ISparkCheckout checkout,
    IOptionsMonitor<SparkTicketingEventsOptions> options,
    TimeProvider clock) : IAttendeeAnswers
{
    /// <summary>Orders whose answers may be given: placed and not ended without a sale.</summary>
    private static readonly OrderState[] OpenStates = [OrderState.PaymentPending, OrderState.Confirmed, OrderState.PartiallyRefunded, OrderState.Disputed, OrderState.DisputeWon];

    public async Task<SparkAttendeeQuestionnaire?> GetAsync(string orderNumber, CancellationToken cancellationToken = default)
    {
        if (await checkout.GetOwnOrderAsync(orderNumber, cancellationToken) is not { } order)
            return null;
        using var session = store.OpenAsyncSession();
        return await BuildAsync(session, order, cancellationToken);
    }

    public async Task<SparkAttendeeQuestionnaire?> SaveAsync(SparkAttendeeAnswersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await checkout.GetOwnOrderAsync(request.OrderNumber, cancellationToken) is not { } order)
            return null;
        if (!OpenStates.Contains(order.State))
            throw AttendeeAnswerException.Closed();
        var line = order.Lines.FirstOrDefault(l => l.LineId == request.LineId && l.Status == OrderLineStatus.Active);
        if (line is null || request.Position < 1 || request.Position > line.Quantity || TicketItemIds.Parse(line.ItemId) is not { } item)
            throw AttendeeAnswerException.NotFound();

        using var session = store.OpenAsyncSession();
        var context = await LineContextAsync(session, item, cancellationToken) ?? throw AttendeeAnswerException.NotFound();
        var now = clock.GetUtcNow();
        if (now >= Deadline(context))
            throw AttendeeAnswerException.Closed();

        var inputs = request.Answers.Select(a => new AttendeeAnswerInput(a.QuestionKey, a.Value, a.Consent)).ToList();
        // Questions asked at checkout must be complete when the buyer submits them; "later" ones may stay open until the deadline.
        var errors = AttendeeQuestionRules.Validate(context.Type.Questions, inputs, EAttendeeQuestionTiming.Checkout);
        if (errors.Count > 0)
            throw new AttendeeAnswerException(errors);

        var id = AttendeeAnswerSet.IdFor(order.Id!, line.LineId, request.Position);
        var set = await session.LoadAsync<AttendeeAnswerSet>(id, cancellationToken) ?? new AttendeeAnswerSet { Id = id };
        Fill(set, order, line, request.Position, context, inputs, request.Language, now);
        await session.StoreAsync(set, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return await BuildAsync(session, order, cancellationToken);
    }

    /// <summary>
    /// GB3 at "Pay" (Checkout's <see cref="ICheckoutPrePaymentCheck"/>): every position of a ticket line whose type asks
    /// required questions at checkout must carry valid answers in <c>Data["attendeeAnswers"]</c>
    /// (<c>[{ lineId, position, answers: [{ questionKey, value, consent }] }]</c>); otherwise "Pay" is refused with
    /// <see cref="TicketingRefusals.AttendeeAnswersRequired"/>. Valid answers are stored with the order, in its transaction.
    /// After the answer deadline nothing is required (the order page no longer asks either).
    /// </summary>
    public async Task CheckAsync(CheckoutPrePaymentContext context, CancellationToken cancellationToken)
    {
        var given = AttendeeCheckoutData.Parse(context.Data);
        var order = context.Order;
        var now = clock.GetUtcNow();
        using var session = store.OpenAsyncSession();
        foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active))
        {
            if (TicketItemIds.Parse(line.ItemId) is not { } item || await LineContextAsync(session, item, cancellationToken) is not { } lineContext)
                continue;
            if (lineContext.Type.Questions.Count == 0 || now >= Deadline(lineContext))
                continue;
            var requiredNow = lineContext.Type.Questions.Any(q => q.Required && q.AskAt == EAttendeeQuestionTiming.Checkout);
            for (var position = 1; position <= line.Quantity; position++)
            {
                var inputs = given.TryGetValue((line.LineId, position), out var answers) ? answers : [];
                if (inputs.Count == 0 && !requiredNow)
                    continue;
                var errors = AttendeeQuestionRules.Validate(lineContext.Type.Questions, inputs, EAttendeeQuestionTiming.Checkout);
                if (errors.Count > 0)
                    throw CheckoutException.Of(TicketingRefusals.AttendeeAnswersRequired, line.LineId, $"{position}:{errors[0].QuestionKey}:{errors[0].Code}");
                var id = AttendeeAnswerSet.IdFor(order.Id!, line.LineId, position);
                var set = new AttendeeAnswerSet { Id = id };
                Fill(set, order, line, position, lineContext, inputs, context.Language, now);
                context.StoreWithOrder(set, id);
            }
        }
    }

    private void Fill(AttendeeAnswerSet set, CheckoutOrder order, CheckoutOrderLine line, int position, LineContext context,
        IReadOnlyList<AttendeeAnswerInput> inputs, string? requestLanguage, DateTimeOffset now)
    {
        set.EventId = context.Event.Id!;
        set.OrganizationId = context.Event.OrganizationId;
        set.OrderId = order.Id!;
        set.OrderNumber = order.Number;
        set.LineId = line.LineId;
        set.Position = position;
        set.TicketTypeId = context.Type.Id!;
        set.DateIds = context.Dates.Select(d => d.Id!).ToList();
        set.BuyerUserId = order.BuyerUserId;
        set.UpdatedAt = now;
        set.EraseAfter = EraseAfter(context);
        var language = string.IsNullOrWhiteSpace(requestLanguage) ? TranslatedString.DefaultLanguage : requestLanguage!;
        foreach (var input in inputs)
        {
            var question = context.Type.Questions.First(q => q.Key == input.QuestionKey);
            set.Answers.RemoveAll(a => a.QuestionKey == question.Key);
            if (AttendeeQuestionRules.IsEmpty(question, input.Value))
                continue;
            var answer = new AttendeeAnswer
            {
                QuestionKey = question.Key,
                Value = AttendeeQuestionRules.Normalize(question, input.Value!),
                Sensitive = question.Sensitive,
            };
            if (question.Sensitive)
            {
                answer.ConsentGivenAt = now;
                answer.ConsentLanguage = language;
                answer.ConsentTextHash = AttendeeQuestionRules.ConsentHash(question.ConsentText!.GetValue(language));
            }
            set.Answers.Add(answer);
        }
    }

    public async Task EraseAsync(string orderId, string lineId, int? position = null, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var sets = await session.Query<AttendeeAnswerSet>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(s => s.OrderId == orderId && s.LineId == lineId)
            .Take(1024)
            .ToListAsync(cancellationToken);
        foreach (var set in sets.Where(s => position is null || s.Position == position))
            session.Delete(set);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task EraseOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var sets = await session.Query<AttendeeAnswerSet>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(s => s.OrderId == orderId)
            .Take(4096)
            .ToListAsync(cancellationToken);
        foreach (var set in sets)
            session.Delete(set);
        await session.SaveChangesAsync(cancellationToken);
    }

    // ---- shape ---------------------------------------------------------------------------------------------------

    private sealed record LineContext(TicketEvent Event, TicketType Type, IReadOnlyList<TicketEventDate> Dates);

    private static async Task<LineContext?> LineContextAsync(IAsyncDocumentSession session, TicketItemRef item, CancellationToken cancellationToken)
    {
        var type = await session.LoadAsync<TicketType>(item.TicketTypeId, cancellationToken);
        if (type is null)
            return null;
        var ev = await session.LoadAsync<TicketEvent>(type.EventId, cancellationToken);
        if (ev is null)
            return null;
        var dateIds = item.IsPass ? type.DateIds : [item.DateId];
        var dates = (await session.LoadAsync<TicketEventDate>(dateIds.Distinct(), cancellationToken)).Values.OfType<TicketEventDate>().ToList();
        return dates.Count == 0 ? null : new LineContext(ev, type, dates);
    }

    private DateTimeOffset Deadline(LineContext context)
    {
        var before = context.Event.AnswerDeadlineHours is { } hours ? TimeSpan.FromHours(Math.Max(0, hours)) : options.CurrentValue.Answers.DefaultDeadlineBeforeStart;
        return context.Dates.Min(d => d.StartsAt) - before;
    }

    private DateTimeOffset EraseAfter(LineContext context)
    {
        var settings = options.CurrentValue.Answers;
        var retention = context.Event.AnswerRetentionDays is { } days ? TimeSpan.FromDays(Math.Max(0, days)) : settings.DefaultRetention;
        if (retention > settings.MaxRetention)
            retention = settings.MaxRetention;
        return context.Dates.Max(d => d.EndsAt ?? d.StartsAt) + retention;
    }

    private async Task<SparkAttendeeQuestionnaire> BuildAsync(IAsyncDocumentSession session, CheckoutOrder order, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lines = new List<SparkAttendeeLine>();
        foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active))
        {
            if (TicketItemIds.Parse(line.ItemId) is not { } item || await LineContextAsync(session, item, cancellationToken) is not { } context)
                continue;
            if (context.Type.Questions.Count == 0)
                continue;
            var deadline = Deadline(context);
            var ids = Enumerable.Range(1, line.Quantity).Select(p => AttendeeAnswerSet.IdFor(order.Id!, line.LineId, p)).ToList();
            var sets = await session.LoadAsync<AttendeeAnswerSet>(ids, cancellationToken);
            var positions = Enumerable.Range(1, line.Quantity).Select(p =>
            {
                var set = sets[AttendeeAnswerSet.IdFor(order.Id!, line.LineId, p)];
                var answers = set?.Answers.Select(a => new SparkAttendeeAnswerView(a.QuestionKey, a.Value, a.ConsentGivenAt is not null)).ToList() ?? [];
                var missing = context.Type.Questions.Where(q => q.Required && !answers.Any(a => a.QuestionKey == q.Key)).Select(q => q.Key).ToList();
                return new SparkAttendeePosition(p, answers, missing);
            }).ToList();
            lines.Add(new SparkAttendeeLine(
                line.LineId,
                line.Title,
                context.Type.Id!,
                context.Type.Questions.Select(ToView).ToList(),
                positions,
                deadline,
                now < deadline && OpenStates.Contains(order.State)));
        }
        return new SparkAttendeeQuestionnaire(order.Number, lines);
    }

    internal static SparkAttendeeQuestion ToView(AttendeeQuestion question) => new(
        question.Key,
        question.Label,
        question.Kind.ToString(),
        question.Choices.Select(c => new SparkAttendeeChoice(c.Key, c.Label)).ToList(),
        question.Required,
        question.AskAt.ToString(),
        question.Sensitive,
        question.Sensitive ? question.ConsentText : null);
}

/// <summary>
/// Erases the answers of an order that will never be attended (GDPR Art. 5(1)(c) and (e): no purpose left): it was
/// cancelled, expired, voided, refunded in full, or lost a dispute. Idempotent, as checkout requires.
/// </summary>
internal sealed class AttendeeAnswerOrderEvents(IAttendeeAnswers answers) : IOrderEventRecipient
{
    public Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
        => orderEvent.Kind is OrderEventKind.Cancelled or OrderEventKind.Expired or OrderEventKind.Voided or OrderEventKind.DisputeLost
            || (orderEvent.Kind == OrderEventKind.Refunded && orderEvent.Order.State == OrderState.Refunded)
            ? answers.EraseOrderAsync(orderEvent.Order.Id!, cancellationToken)
            : Task.CompletedTask;
}
