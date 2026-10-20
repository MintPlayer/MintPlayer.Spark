using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;

[assembly: EndpointsMethodName("MapSparkIdentityProviderEndpoints")]

// The TypeScript contracts of the portal API ng-spark's identity-provider pages read (`/spark/identity-provider/*`,
// commerce X20 item 2, docs/guide-typescript-contracts.md). The records keep their C# names; [SparkContract("Spark…")]
// gives each the name the client already used.
[assembly: SparkClientModule("MintPlayer.Spark.IdentityProvider.Endpoints.Portal", "@mintplayer/ng-spark/identity-provider/core")]
