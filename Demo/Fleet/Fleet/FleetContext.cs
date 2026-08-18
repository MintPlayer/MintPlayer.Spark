using Fleet.Entities;
using Fleet.Replicated;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace Fleet;

public partial class FleetContext : SparkContext
{
    public IRavenQueryable<Car> Cars => Session.Query<Car>();
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
