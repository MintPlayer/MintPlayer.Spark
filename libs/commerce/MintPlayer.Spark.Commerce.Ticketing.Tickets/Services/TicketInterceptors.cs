using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>The event a settings or list document belongs to: it must exist, and a saved document never moves to another event.</summary>
internal static class TicketEventChild
{
    public static async Task<TicketEvent> AdoptAsync(IAsyncDocumentSession session, string eventId, string? previousEventId, string attribute)
    {
        if (previousEventId is not null && !string.Equals(previousEventId, eventId, StringComparison.Ordinal))
            throw new SparkValidationException("This belongs to its event and cannot move to another one.", attribute);
        if (string.IsNullOrEmpty(eventId) || await session.LoadAsync<TicketEvent>(eventId) is not { } ev)
            throw new SparkValidationException("Choose an event.", attribute);
        return ev;
    }
}

/// <summary>
/// Saves of an event's ticket settings (D11, D30, GB1, GC7): one per event, the organization copied from the event, the
/// lead time, fee and freeze within the configured bounds, the template colours as <c>#rrggbb</c>. The logo and
/// background flags are kept: only the template-image endpoint sets them (it stores the JPEG).
/// </summary>
internal sealed class TicketEventSettingsInterceptor(IOptionsMonitor<SparkTicketingTicketsOptions> options, TimeProvider clock) : IBeforeSave<TicketEventSettings>
{
    public async ValueTask OnBeforeSaveAsync(TicketEventSettings entity, SaveContext context)
    {
        var session = context.GetSession();
        var before = context.Before as TicketEventSettings;
        var ev = await TicketEventChild.AdoptAsync(session, entity.EventId, before?.EventId, nameof(TicketEventSettings.EventId));
        entity.OrganizationId = ev.OrganizationId;
        if (before is null)
        {
            var eventId = entity.EventId;
            if (await session.Query<TicketEventSettings>().Customize(c => c.WaitForNonStaleResults()).AnyAsync(s => s.EventId == eventId))
                throw new SparkValidationException("This event has its ticket settings already.", nameof(TicketEventSettings.EventId));
        }
        entity.HasLogo = before?.HasLogo ?? false;
        entity.HasBackground = before?.HasBackground ?? false;

        if (Problem(entity, options.CurrentValue) is { } problem)
            throw new SparkValidationException(problem.Message, problem.Attribute);
        entity.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>The first rule <paramref name="settings"/> breaks, or null. Pure.</summary>
    internal static (string Message, string Attribute)? Problem(TicketEventSettings settings, SparkTicketingTicketsOptions options)
    {
        if (settings.DeliveryLeadTimeHours is { } hours && (hours < 0 || TimeSpan.FromHours(hours) > options.Delivery.MaxLeadTime))
            return ($"The lead time lies between 0 and {options.Delivery.MaxLeadTime.TotalHours:0} hours.", nameof(TicketEventSettings.DeliveryLeadTimeHours));
        if (settings.TransferFeeBasisPoints is { } fee && (fee < 0 || fee > options.Exchange.MaxTransferFeeBasisPoints))
            return ($"The transfer fee lies between 0 and {options.Exchange.MaxTransferFeeBasisPoints} basis points.", nameof(TicketEventSettings.TransferFeeBasisPoints));
        if (settings.FreezeBeforeDoorsMinutes is < 0 or > 60 * 24 * 30)
            return ("The freeze before doors lies between 0 and 30 days.", nameof(TicketEventSettings.FreezeBeforeDoorsMinutes));
        if (settings.TemplateAccentColor is { Length: > 0 } accent && !TicketPdfWriter.IsColor(accent))
            return ("Use a colour as #rrggbb.", nameof(TicketEventSettings.TemplateAccentColor));
        if (settings.TemplateAccentTextColor is { Length: > 0 } text && !TicketPdfWriter.IsColor(text))
            return ("Use a colour as #rrggbb.", nameof(TicketEventSettings.TemplateAccentTextColor));
        return null;
    }
}

/// <summary>
/// Saves of check-in lists (GB3): the organization copied from the event; the dates and ticket types it names must be
/// the event's own; a zone names the ticket types it admits.
/// </summary>
internal sealed class TicketCheckInListInterceptor : IBeforeSave<TicketCheckInList>
{
    public async ValueTask OnBeforeSaveAsync(TicketCheckInList entity, SaveContext context)
    {
        var session = context.GetSession();
        var before = context.Before as TicketCheckInList;
        var ev = await TicketEventChild.AdoptAsync(session, entity.EventId, before?.EventId, nameof(TicketCheckInList.EventId));
        entity.OrganizationId = ev.OrganizationId;
        entity.Name = entity.Name?.Trim() ?? "";
        entity.DateIds = entity.DateIds.Distinct(StringComparer.Ordinal).ToList();
        entity.TicketTypeIds = entity.TicketTypeIds.Distinct(StringComparer.Ordinal).ToList();

        var eventId = ev.Id!;
        var dateIds = (await session.Query<TicketEventDate>().Where(d => d.EventId == eventId).Select(d => d.Id).ToListAsync()).ToHashSet(StringComparer.Ordinal);
        var typeIds = (await session.Query<TicketType>().Where(t => t.EventId == eventId).Select(t => t.Id).ToListAsync()).ToHashSet(StringComparer.Ordinal);
        if (Problem(entity, dateIds!, typeIds!) is { } problem)
            throw new SparkValidationException(problem.Message, problem.Attribute);
    }

    /// <summary>The first rule <paramref name="list"/> breaks, or null. Pure.</summary>
    internal static (string Message, string Attribute)? Problem(TicketCheckInList list, IReadOnlySet<string> eventDateIds, IReadOnlySet<string> eventTypeIds)
    {
        if (string.IsNullOrWhiteSpace(list.Name) || list.Name.Length > 80)
            return ("Name the list (at most 80 characters).", nameof(TicketCheckInList.Name));
        if (list.DateIds.FirstOrDefault(d => !eventDateIds.Contains(d)) is { } date)
            return ($"'{date}' is not a date of this event.", nameof(TicketCheckInList.DateIds));
        if (list.TicketTypeIds.FirstOrDefault(t => !eventTypeIds.Contains(t)) is { } type)
            return ($"'{type}' is not a ticket type of this event.", nameof(TicketCheckInList.TicketTypeIds));
        if (list.Kind == ETicketCheckInListKind.Zone && list.TicketTypeIds.Count == 0)
            return ("A zone names the ticket types it admits.", nameof(TicketCheckInList.TicketTypeIds));
        return null;
    }
}
