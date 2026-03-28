using Fleet.Entities;
using Fleet.Indexes;
using Fleet.Replicated;
using MintPlayer.Spark;

namespace Fleet;

public class FleetContext : SparkContext
{
    public IQueryable<Car> Cars => Session.Query<Car>();
    public IQueryable<VCar> VCars => Session.Query<VCar>(nameof(Cars_Overview));
    public IQueryable<Person> People => Session.Query<Person>();
    public IQueryable<Company> Companies => Session.Query<Company>();
}
