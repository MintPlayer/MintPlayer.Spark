using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Actions;

// The event types are organization-owned (X8): Organizations' row policy limits every list and load to the members of
// the owning organization (Viewer reads, Member edits, Admin deletes and publishes), and a published event to everyone
// the type's right reaches. The storefront reads published events through /spark/ticketing/events/public instead.

/// <summary>The query <c>TicketEvents</c>.</summary>
public partial class TicketEventActions : DefaultPersistentObjectActions<TicketEvent>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketEvent> TicketEvents() => session.Query<TicketEvent>();
}

/// <summary>The query <c>TicketEventDates</c>.</summary>
public partial class TicketEventDateActions : DefaultPersistentObjectActions<TicketEventDate>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketEventDate> TicketEventDates() => session.Query<TicketEventDate>();
}

/// <summary>The query <c>TicketTypes</c>.</summary>
public partial class TicketTypeActions : DefaultPersistentObjectActions<TicketType>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketType> TicketTypes() => session.Query<TicketType>();
}

/// <summary>The query <c>TicketSalePhases</c>.</summary>
public partial class TicketSalePhaseActions : DefaultPersistentObjectActions<TicketSalePhase>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketSalePhase> TicketSalePhases() => session.Query<TicketSalePhase>();
}
