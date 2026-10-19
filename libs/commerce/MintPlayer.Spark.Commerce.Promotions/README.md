# MintPlayer.Spark.Commerce.Promotions

Promotions for the MintPlayer.Spark commerce suite (alias `commerce-promotions`, ng entry point
`@mintplayer/ng-spark/commerce/promotions`):

- **codes**: percentage or fixed amount off (spread pro rata over the order lines), unlock hidden items and presale
  phases, capacity-blocking quotas; usage limits, validity windows, once per buyer;
- **the ticket waiting list**: after a sell-out, freed capacity is offered in order as personal, time-limited codes;
- **back-in-stock notifications** for shops (GB11);
- **seller sale prices** with the Omnibus prior price (GA3, PID Art. 6a);
- **promoter and affiliate tracking links** with attribution, per-link reports and commissions (GC2).

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddCommerceCheckout();
    spark.AddCommercePromotions(); // Spark:Commerce:Promotions
});
```

Checkout never references this package: it plugs in through Checkout's pricing steps, order-event recipients,
capacity sources and admission gate. The full guide is [docs/guide-commerce-promotions.md](../../../docs/guide-commerce-promotions.md).
