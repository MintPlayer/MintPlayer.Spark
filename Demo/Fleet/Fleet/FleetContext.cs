using Fleet.Entities;
using Fleet.Indexes;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace Fleet;

public class FleetContext : SparkContext
{
    public IRavenQueryable<Car> Cars => Session.Query<Car>();
    public IRavenQueryable<VCar> VCars => Session.Query<VCar, Cars_Overview>();
}
