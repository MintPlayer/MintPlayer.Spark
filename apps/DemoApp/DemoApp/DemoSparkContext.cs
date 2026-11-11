using DemoApp.Library.Entities;
using MintPlayer.Spark;

namespace DemoApp;

public partial class DemoSparkContext : SparkContext
{
    public IQueryable<Person> People => Session.Query<Person>();
    public IQueryable<Company> Companies => Session.Query<Company>();
    public IQueryable<Car> Cars => Session.Query<Car>();
    public IQueryable<Stock> Stocks => Session.Query<Stock>();

    // Plain entity roots; Bicycles is also the query Person.Bicycle's query-less reference resolves to.
    public IQueryable<Bicycle> Bicycles => Session.Query<Bicycle>();
    public IQueryable<Motorcycle> Motorcycles => Session.Query<Motorcycle>();
}
