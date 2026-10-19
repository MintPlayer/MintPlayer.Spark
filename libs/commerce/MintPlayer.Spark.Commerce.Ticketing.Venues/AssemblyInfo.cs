using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkTicketingVenuesEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's ticketing/venues entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md). The root namespace: the plan types (Models) and the wire records (Contracts).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Ticketing.Venues", "@mintplayer/ng-spark/ticketing/venues")]
