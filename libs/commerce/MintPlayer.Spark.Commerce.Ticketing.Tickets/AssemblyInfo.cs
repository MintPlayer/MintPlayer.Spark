using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkTicketingTicketsEndpoints")]

// The TypeScript contracts of the endpoints ng-spark's ticketing/tickets entry point reads (X20 item 2,
// docs/guide-typescript-contracts.md).
[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts", "@mintplayer/ng-spark/ticketing/tickets")]
