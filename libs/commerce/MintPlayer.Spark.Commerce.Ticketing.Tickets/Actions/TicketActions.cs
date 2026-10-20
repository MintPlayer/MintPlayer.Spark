using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Actions;

// Every ticket type is organization-owned (X8): Organizations' row policy limits each list and load to the members of
// the seller organization. Tickets, scans and devices are read-only through the generic pages (security.json grants
// QueryRead only): this package's services write them. Ticket documents never show the secret's verifier (the model
// declares no such attribute), and the secret itself lives in TicketSecrets, which has no persistent object at all.

/// <summary>The query <c>Tickets</c>: the seller's tickets with their current holder only (§4.11).</summary>
public partial class TicketActions : DefaultPersistentObjectActions<Ticket>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<Ticket> Tickets() => session.Query<Ticket>();
}

/// <summary>The query <c>TicketEventSettings</c>.</summary>
public partial class TicketEventSettingsActions : DefaultPersistentObjectActions<TicketEventSettings>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketEventSettings> TicketEventSettings() => session.Query<TicketEventSettings>();
}

/// <summary>The query <c>TicketCheckInLists</c> (GB3).</summary>
public partial class TicketCheckInListActions : DefaultPersistentObjectActions<TicketCheckInList>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketCheckInList> TicketCheckInLists() => session.Query<TicketCheckInList>();
}

/// <summary>The query <c>TicketScannerDevices</c>: registered door devices (revoked through the scanner endpoints).</summary>
public partial class TicketScannerDeviceActions : DefaultPersistentObjectActions<TicketScannerDevice>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketScannerDevice> TicketScannerDevices() => session.Query<TicketScannerDevice>();
}

/// <summary>The query <c>TicketScans</c>: the gate log and its double entries (S9).</summary>
public partial class TicketScanActions : DefaultPersistentObjectActions<TicketScan>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<TicketScan> TicketScans() => session.Query<TicketScan>();
}
