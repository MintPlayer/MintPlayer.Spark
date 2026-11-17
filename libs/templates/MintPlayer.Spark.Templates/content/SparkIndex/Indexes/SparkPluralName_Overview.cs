using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace SparkNs.Indexes;

/// <summary>
/// The RavenDB index SparkItem's queries run on, with <see cref="VSparkItemOverview"/> describing its rows.
/// </summary>
/// <remarks>
/// <para>
/// Spark deploys it at startup. <c>[DefaultIndex]</c> makes it the index SparkItem's queries use, and its view is
/// then SparkItem's grid: every view property is a column, and an attribute the view lacks shows on the detail
/// page only. A query sorted by an attribute the view lacks fails <c>--spark-verify-model</c>.
/// </para>
/// <para>
/// The Map projects <c>Name</c>, the property <c>dotnet new spark-entity</c> starts an entity with; project the
/// columns the grid should show. Add a field to the Map and the same property to the view: the analyzer reports
/// one the other lacks. Then run <c>dotnet run -- --spark-synchronize-model</c>.
/// </para>
/// </remarks>
[DefaultIndex]
public partial class SparkPluralName_Overview : SparkIndexCreationTask<SparkItem>
{
    public SparkPluralName_Overview()
    {
        Map = items => from item in items
                       select new VSparkItemOverview
                       {
                           Id = item.Id,
                           Name = item.Name,
                           // [Search] on Name generates this full-text companion; the Map fills it.
                           NameSearch = item.Name,
                       };

        StoreAllFields(FieldStorage.Yes);
    }
}

/// <summary>A row of <see cref="SparkPluralName_Overview"/>: SparkItem's grid columns.</summary>
[FromIndex(typeof(SparkPluralName_Overview))]
public partial class VSparkItemOverview
{
    public string? Id { get; set; }

    /// <summary>The name it is listed and found by.</summary>
    [Search]
    public string Name { get; set; } = string.Empty;
}
