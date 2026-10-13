using DemoApp.Library.Entities;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

/// <summary>
/// One overview over two collections: a multi-map that is the default index of <see cref="Bicycle"/> <b>and</b>
/// <see cref="Motorcycle"/> (<c>docs/default_index_PRD.md</c>, D1, D4).
/// </summary>
/// <remarks>
/// <para>
/// A multi-map's marker must name its types: a referenced assembly carries no constructor bodies, so nothing could
/// read the <c>AddMap</c> calls from there. The analyzer checks each listed type against an <c>AddMap</c> here
/// (SPARK053), and a single-map's marker may only name its own <c>T</c> (SPARK051).
/// </para>
/// <para>
/// It derives from <see cref="SparkMultiMapIndexCreationTask{TReduceResult}"/>, which writes a collection field into
/// every map, so <c>GetBicycles</c> narrows the index to bicycles before paging, totals and row security apply.
/// Without that a motorcycle would be listed as a bicycle. Both lists share <see cref="VTwoWheeler"/> as their view,
/// and <c>Person.Bicycle</c>'s picker is served by this index through the plain <c>Bicycles</c> root.
/// </para>
/// </remarks>
[DefaultIndex(typeof(Bicycle), typeof(Motorcycle))]
public partial class TwoWheelers_Overview : SparkMultiMapIndexCreationTask<VTwoWheeler>
{
    public TwoWheelers_Overview()
    {
        AddMap<Bicycle>(bicycles => from bicycle in bicycles
                                    select new
                                    {
                                        bicycle.Id,
                                        bicycle.Brand,
                                        BrandSearch = bicycle.Brand,
                                        bicycle.Model,
                                        Details = bicycle.FrameSize + " cm frame",
                                    });
        AddMap<Motorcycle>(motorcycles => from motorcycle in motorcycles
                                          select new
                                          {
                                              motorcycle.Id,
                                              motorcycle.Brand,
                                              BrandSearch = motorcycle.Brand,
                                              motorcycle.Model,
                                              Details = motorcycle.EngineCapacity + " cc",
                                          });

        StoreAllFields(FieldStorage.Yes);
    }
}
