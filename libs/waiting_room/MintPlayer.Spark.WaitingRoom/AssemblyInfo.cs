using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkWaitingRoomEndpoints")]
[assembly: SparkClientModule("MintPlayer.Spark.WaitingRoom.Contracts", "@mintplayer/ng-spark/waiting-room")]
