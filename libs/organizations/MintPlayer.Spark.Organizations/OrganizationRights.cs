namespace MintPlayer.Spark.Organizations;

/// <summary>The rights vocabulary the organizations package reads from <c>security.json</c>.</summary>
public static class OrganizationRights
{
    /// <summary>
    /// <c>AllOrganizations/{Type}</c>: the holder sees every organization's rows of <c>{Type}</c> (Query and Read), not only
    /// those of the organizations they belong to (warehouse WM3, lead decision 2026-10-20). Granted per type, typically to a
    /// slot a library declares (a warehouse's staff); without it, the membership rule applies as before.
    /// </summary>
    /// <example><c>{ "resource": "AllOrganizations/WarehouseItem", "groupId": "warehouse:staff" }</c></example>
    public const string AllOrganizations = "AllOrganizations";
}
