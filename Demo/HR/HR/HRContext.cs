using HR.Entities;
using HR.Indexes;
using HR.Replicated;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace HR;

public class HRContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<VPerson> VPeople => Session.Query<VPerson, People_Overview>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
    public IRavenQueryable<Car> Cars => Session.Query<Car>();
}
