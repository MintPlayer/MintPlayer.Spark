using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkChallengesEndpoints")]
[assembly: SparkClientModule("MintPlayer.Spark.Challenges.Contracts", "@mintplayer/ng-spark/challenges")]
