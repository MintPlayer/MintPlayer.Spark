# MintPlayer.Spark.Commerce.Ticketing.Tickets

Tickets and the door scanner for the MintPlayer.Spark ticketing suite (alias `ticketing-tickets`, Angular entry point
`@mintplayer/ng-spark/ticketing/tickets`).

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMolliePayments();      // or AddStripePayments(), AddTestPayments()
    spark.AddMailManager();         // ticket mails with the PDF and .ics (optional)
    spark.AddTicketing();           // events, venues, tickets and the scanner
});
```

- One ticket per unit of a confirmed order line, with a 128-bit secret stored as PBKDF2-HMAC-SHA256 (50,000
  iterations, S8). The QR code is `ST1.{code}.{secret}`.
- Delivery at the seller's lead time; the ticket page and a mailed PDF (per-event template, GC7) with an `.ics` per date (GB9).
- Named tickets, transfer and face-value resale; both reissue the ticket and erase the previous holder's data from it.
- Revocation on refunds, the dispute freeze ("on hold") and revocation on a lost dispute (GB1); comps, guest lists,
  re-send, seat change and upgrade follow Checkout's back-office order events (GB7).
- Check-in lists per gate, day or zone, multi-day passes, re-entry (GB3); an "ID check" banner for age-restricted types (GB12).
- The scanner: event-scoped device tokens, online atomic check-in, an offline PBKDF2 list and sync with double-entry flags (S9).

See [`docs/guide-ticketing-tickets.md`](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-ticketing-tickets.md).
