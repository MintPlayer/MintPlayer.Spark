using DemoApp.Library.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace DemoApp;

public partial class DemoSparkContext : SparkContext
{
    public IQueryable<Person> People => Session.Query<Person>();
    public IQueryable<Company> Companies => Session.Query<Company>();
    public IQueryable<Car> Cars => Session.Query<Car>();
    public IQueryable<Stock> Stocks => Session.Query<Stock>();

    // Plain entity roots over one multi-map default index (Indexes/TwoWheelers_Overview.cs). Each mints its
    // Get{Property} query, stamped with that index and narrowed to its own collection; Bicycles is also the query
    // Person.Bicycle's query-less reference resolves to. The view root VTwoWheelers is generated (D6).
    public IQueryable<Bicycle> Bicycles => Session.Query<Bicycle>();
    public IQueryable<Motorcycle> Motorcycles => Session.Query<Motorcycle>();
}
