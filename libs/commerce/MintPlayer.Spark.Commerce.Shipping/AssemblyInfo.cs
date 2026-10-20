using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkCommerceShippingEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's commerce/shipping entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Shipping.Contracts", "@mintplayer/ng-spark/commerce/shipping")]
