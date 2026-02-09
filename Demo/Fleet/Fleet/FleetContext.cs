using Fleet.Entities;
using Fleet.Indexes;
using Fleet.Replicated;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace Fleet;

public class FleetContext : SparkContext
{
    public IRavenQueryable<Car> Cars => Session.Query<Car>();
    public IRavenQueryable<VCar> VCars => Session.Query<VCar, Cars_Overview>();
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
