namespace SparkNs.Entities;

/// <summary>The SparkItem entity. Each public property becomes an attribute of the model type, described by its summary.</summary>
/// <remarks>
/// To make it a model type:
/// <list type="number">
/// <item>Expose it on the app's SparkContext:
/// <c>public IQueryable&lt;SparkItem&gt; SparkPluralName =&gt; Session.Query&lt;SparkItem&gt;();</c></item>
/// <item>Run <c>dotnet run -- --spark-synchronize-model</c> in the app project, and commit what it writes
/// (App_Data/Model/SparkItem.json and modelHashes.json).</item>
/// <item>Grant it in App_Data/security.json, for example
/// <c>{ "key": "sparkitem", "resource": "QueryReadEditNewDelete/SparkItem", "groupId": "@authenticated" }</c>,
/// and give it an actions class that says which rows a caller sees (<c>dotnet new spark-actions -n SparkItem</c>).</item>
/// </list>
/// </remarks>
public class SparkItem
{
    /// <summary>Unique identifier, assigned when it is first saved.</summary>
    public string? Id { get; set; }

    /// <summary>The name it is listed and found by.</summary>
    public string Name { get; set; } = string.Empty;
}
