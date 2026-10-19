# MintPlayer.Spark.Commerce.Ticketing.Events

Events for the MintPlayer.Spark ticketing suite (alias `ticketing-events`, Angular entry point
`@mintplayer/ng-spark/ticketing/events`): Events → Dates → Sale phases, ticket types and their prices per price
category, per-sale policies within the channel's bounds, attendee questions, PreSelectOrder and the public event page.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMolliePayments();      // or AddStripePayments(), AddTestPayments()
    spark.AddTicketingEvents();     // adds Checkout and Ticketing.Venues
    spark.AddWaitingRoom();         // for queued phases
});
```

- Ticket types sell through Checkout as an `ICheckoutCapacitySource` (item ids `ticketing:{phase}|{type}|{category}|{date}`).
- Seated dates freeze a Ticketing.Venues layout snapshot when the event is published.
- Published events get server-rendered meta tags, schema.org `Event` data, a sitemap entry and an `.ics` per date.

See [`docs/guide-ticketing-events.md`](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-ticketing-events.md).
