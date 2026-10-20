# MintPlayer.Spark.Commerce.Invoicing.Peppol.Test

An in-memory Peppol access point (`test`) for `MintPlayer.Spark.Commerce.Invoicing`: development, demos and tests. It sends
nothing.

```csharp
spark.AddCommerceInvoicing().UseTestPeppol(o =>
{
    o.Directory.Add("0208:0123456789");   // receivers that can receive; empty = everyone
    o.Refuse.Add("0208:0999999999");      // refused at the access point
    o.OutagesRemaining = 1;               // the next send fails with a retryable outage
});
```

`TestPeppolAccessPoint.Submissions` lists every UBL it was handed (idempotent per document id). The UBL is validated by
Invoicing (the EN 16931 rule subset and any registered `IEInvoiceValidator`) before it reaches the adapter.
