using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkTicketingEventsEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's ticketing/events entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Ticketing.Events.Contracts", "@mintplayer/ng-spark/ticketing/events")]
