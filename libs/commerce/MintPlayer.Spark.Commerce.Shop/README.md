# MintPlayer.Spark.Commerce.Shop

The shop of the MintPlayer.Spark commerce suite (alias `commerce-shop`, Angular entry point
`@mintplayer/ng-spark/commerce/shop`): products with variants (each its own quota), categories, typo-tolerant search
with facet counts, the checkout delivery step, Paid → Shipped → Delivered, returns, verified-purchase reviews and
product feeds.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMolliePayments();      // or AddStripePayments(), AddTestPayments()
    spark.AddCommerceShipping().UseSendcloud();  // or another carrier adapter
    spark.AddCommerceShop();        // adds Checkout and Shipping
});
```

- Variants sell through Checkout as an `ICheckoutCapacitySource` (item ids `shop:{variantId}`), with their refund terms
  (`IRefundTermsSource`) and the standard delivery cost a withdrawal refunds (`IStandardDeliveryCost`, CRD Art. 13(2)).
- The delivery step is a pricing step over Shipping's rates; delivery reports start the withdrawal period at the last
  delivery (CRD Art. 9(2)(b)(i)) and the legal guarantee.
- Products carry unit prices (GA3), GPSR safety information (GA7), guarantee information (GA8), HS code and origin (GB6),
  a minimum age (GB12), galleries and facets (GB13).
- Published products get server-rendered meta tags, schema.org `Product` data, a sitemap entry and Google Merchant /
  Meta feeds (GC6).

See [`docs/guide-commerce-shop.md`](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-commerce-shop.md).
