using MintPlayer.AspNetCore.Endpoints;
[assembly: EndpointsMethodName("MapSparkHistoryEndpoints")]

// The verbs History asks for are reserved: no custom action may be named History or Revert.
[assembly: MintPlayer.Spark.Abstractions.Authorization.SparkReservedActions(typeof(MintPlayer.Spark.History.HistoryRights))]
