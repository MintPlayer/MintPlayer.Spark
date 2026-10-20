using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkCommerceInvoicingEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's commerce/invoicing entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Invoicing.Contracts", "@mintplayer/ng-spark/commerce/invoicing")]
