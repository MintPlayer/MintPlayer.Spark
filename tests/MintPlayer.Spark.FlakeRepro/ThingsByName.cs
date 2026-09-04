using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.FlakeRepro;

/// <summary>A document with enough shape to make indexing it cost something.</summary>
public class Thing
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int Value { get; set; }
}

/// <summary>
/// One static index per database, so each cycle costs the server what a real test costs it.
/// </summary>
/// <remarks>
/// Deploying an index is a cluster command in its own right, and a database carrying an index is
/// more work to delete than an empty one. Sequential empty create/delete cycles under full CPU load
/// produced a slowest deletion of 0.1s, and adding concurrency alone only reached 0.7s — neither is
/// close to the 15s wait. The difference from a real run is that a real database is not idle.
/// </remarks>
public class Things_ByName : AbstractIndexCreationTask<Thing>
{
    public Things_ByName()
    {
        Map = things => from thing in things
                        select new
                        {
                            thing.Name,
                            thing.Category,
                            thing.Value,
                        };

        Index(x => x.Name, FieldIndexing.Search);
    }
}
