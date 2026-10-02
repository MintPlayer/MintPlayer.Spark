using MintPlayer.AspNetCore.Endpoints;
[assembly: EndpointsMethodName("MapSparkSoftDeleteEndpoints")]

// The verbs SoftDelete asks for are reserved: no custom action may be named Restore, Purge or ViewDeleted.
// Declared here rather than in SoftDelete.Abstractions, which references no Spark package.
[assembly: MintPlayer.Spark.Abstractions.Authorization.SparkReservedActions(typeof(MintPlayer.Spark.SoftDelete.SoftDeleteRights))]
