using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>Builders for the ticketing-tickets tests (M15). Secrets use 1,000 PBKDF2 iterations to keep the tests fast.</summary>
internal static class TicketFixtures
{
    public const int TestIterations = 1_000;

    public const string EventId = "TicketEvents/m15";

    public const string Organization = "Organizations/seller";

    public static readonly DateTimeOffset Doors = new(2026, 7, 1, 17, 0, 0, TimeSpan.Zero);

    public static SparkTicketingTicketsOptions Options(Action<SparkTicketingTicketsOptions>? configure = null)
    {
        var options = new SparkTicketingTicketsOptions();
        options.Secrets.Iterations = TestIterations;
        configure?.Invoke(options);
        return options;
    }

    /// <summary>An event with <paramref name="dates"/> dates one day apart (doors at <see cref="Doors"/>), a standard and a VIP type.</summary>
    public static async Task<(List<string> DateIds, string StandardTypeId, string VipTypeId)> SeedEventAsync(IDocumentStore store, int dates = 1)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new TicketEvent
        {
            Id = EventId, OrganizationId = Organization, IsPublished = true, Name = TranslatedString.Of("en", "Festival"), Slug = "festival", Channel = "tickets",
        }, EventId);
        var dateIds = new List<string>();
        for (var i = 0; i < dates; i++)
        {
            var id = $"TicketEventDates/m15-{i + 1}";
            await session.StoreAsync(new TicketEventDate
            {
                Id = id, EventId = EventId, OrganizationId = Organization, Capacity = 1_000,
                DoorsOpenAt = Doors.AddDays(i), StartsAt = Doors.AddDays(i).AddHours(1), EndsAt = Doors.AddDays(i).AddHours(7),
            }, id);
            dateIds.Add(id);
        }
        await session.StoreAsync(new TicketType { Id = "TicketTypes/standard", EventId = EventId, OrganizationId = Organization, Name = TranslatedString.Of("en", "Standard") }, "TicketTypes/standard");
        await session.StoreAsync(new TicketType { Id = "TicketTypes/vip", EventId = EventId, OrganizationId = Organization, Name = TranslatedString.Of("en", "VIP") }, "TicketTypes/vip");
        await session.SaveChangesAsync();
        return (dateIds, "TicketTypes/standard", "TicketTypes/vip");
    }

    /// <summary>A ticket of the seeded event, as issuance writes it, with its secret.</summary>
    public static (Ticket Ticket, string Secret) NewTicket(IReadOnlyList<string> dateIds, string typeId = "TicketTypes/standard", int iterations = TestIterations, DateTimeOffset? at = null)
    {
        var code = TicketSecrets.NewCode();
        var secret = TicketSecrets.NewSecret();
        var verifier = TicketSecrets.Hash(secret, iterations);
        var now = at ?? Doors.AddDays(-10);
        return (new Ticket
        {
            Id = Ticket.IdFor(code), Code = code, OrganizationId = Organization, EventId = EventId, DateIds = [.. dateIds],
            TicketTypeId = typeId, ItemId = "ticketing:test", OrderId = "CheckoutOrders/1", OrderNumber = "T-1", LineId = "L1", Position = 1,
            SourceOrderId = "CheckoutOrders/1", SourceLineId = "L1", SourcePosition = 1, HolderName = "Holder", HolderEmail = "holder@example.test",
            SecretSalt = verifier.Salt, SecretHash = verifier.Hash, SecretIterations = verifier.Iterations, Generation = 1,
            DeliverAt = now, IssuedAt = now, UpdatedAt = now,
        }, secret);
    }

    public static async Task<List<(Ticket Ticket, string Secret)>> StoreTicketsAsync(IDocumentStore store, IReadOnlyList<string> dateIds, int count, string typeId = "TicketTypes/standard", int iterations = TestIterations)
    {
        var tickets = Enumerable.Range(0, count).Select(_ => NewTicket(dateIds, typeId, iterations)).ToList();
        using (var bulk = store.BulkInsert())
        {
            foreach (var (ticket, _) in tickets)
                await bulk.StoreAsync(ticket, ticket.Id);
        }
        return tickets;
    }

    /// <summary>A door device of the seeded event on <paramref name="listId"/> (stored directly; registration needs the scanner role).</summary>
    public static async Task<TicketScannerDevice> StoreDeviceAsync(IDocumentStore store, string listId, string name, string? token = null, DateTimeOffset? expires = null)
    {
        var key = Guid.NewGuid().ToString("N");
        var device = new TicketScannerDevice
        {
            Id = "TicketScannerDevices/" + key, OrganizationId = Organization, EventId = EventId, ListId = listId, Name = name,
            TokenHash = TicketSecrets.TokenHash(token ?? TicketSecrets.NewToken()), CreatedBy = "Users/staff", CreatedAt = Doors.AddDays(-1),
            ExpiresAt = expires ?? Doors.AddDays(5),
        };
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(device, device.Id);
        await session.SaveChangesAsync();
        return device;
    }

    public static string Uuid() => Guid.NewGuid().ToString();
}
