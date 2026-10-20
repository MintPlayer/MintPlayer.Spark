using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkCommercePaymentsEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's commerce/payments entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Payments.Contracts", "@mintplayer/ng-spark/commerce/payments")]
