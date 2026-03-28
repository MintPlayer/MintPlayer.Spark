using DemoApp.Library.Entities;
using MintPlayer.Spark;

namespace DemoApp;

public class DemoSparkContext : SparkContext
{
    public IQueryable<Person> People => Session.Query<Person>();
    public IQueryable<Company> Companies => Session.Query<Company>();
    public IQueryable<Car> Cars => Session.Query<Car>();
    public IQueryable<Stock> Stocks => Session.Query<Stock>();
}
