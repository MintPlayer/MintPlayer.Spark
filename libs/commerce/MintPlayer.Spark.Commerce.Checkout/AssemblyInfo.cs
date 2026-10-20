using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkCommerceCheckoutEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's commerce/checkout entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Checkout.Contracts", "@mintplayer/ng-spark/commerce/checkout")]
