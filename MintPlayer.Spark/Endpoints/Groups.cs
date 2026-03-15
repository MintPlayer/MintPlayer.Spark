using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Endpoints;

internal class SparkGroup : IEndpointGroup
{
    public static string Prefix => "/spark";
}

internal class EntityTypesGroup : IEndpointGroup, IMemberOf<SparkGroup>
{
    public static string Prefix => "/types";
}

internal class QueriesGroup : IEndpointGroup, IMemberOf<SparkGroup>
{
    public static string Prefix => "/queries";
}

internal class PersistentObjectGroup : IEndpointGroup, IMemberOf<SparkGroup>
{
    public static string Prefix => "/po";
}

internal class ActionsGroup : IEndpointGroup, IMemberOf<SparkGroup>
{
    public static string Prefix => "/actions";
}

internal class LookupReferencesGroup : IEndpointGroup, IMemberOf<SparkGroup>
{
    public static string Prefix => "/lookupref";
}
