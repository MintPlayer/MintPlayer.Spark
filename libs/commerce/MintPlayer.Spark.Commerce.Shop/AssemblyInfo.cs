using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkCommerceShopEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's commerce/shop entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Shop.Contracts", "@mintplayer/ng-spark/commerce/shop")]
