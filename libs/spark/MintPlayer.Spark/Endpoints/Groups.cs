using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Endpoints;

internal class SparkGroup : IEndpointGroup
{
    public static string Prefix => "/spark";
}

[MemberOf<SparkGroup>]
internal class EntityTypesGroup : IEndpointGroup
{
    public static string Prefix => "/types";
}

[MemberOf<SparkGroup>]
internal class QueriesGroup : IEndpointGroup
{
    public static string Prefix => "/queries";
}

[MemberOf<SparkGroup>]
internal class PersistentObjectGroup : IEndpointGroup
{
    public static string Prefix => "/po";
}

[MemberOf<SparkGroup>]
internal class ActionsGroup : IEndpointGroup
{
    public static string Prefix => "/actions";
}

[MemberOf<SparkGroup>]
internal class LookupReferencesGroup : IEndpointGroup
{
    public static string Prefix => "/lookupref";
}
