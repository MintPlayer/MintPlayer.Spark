using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkCommercePromotionsEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's commerce/promotions entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Promotions.Contracts", "@mintplayer/ng-spark/commerce/promotions")]
