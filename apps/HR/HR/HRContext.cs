using HR.Entities;
using HR.Indexes;
using HR.Replicated;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace HR;

public partial class HRContext : SparkContext
{
    public IQueryable<Person> People => Session.Query<Person>();
    public IQueryable<VPerson> VPeople => Session.Query<VPerson, People_Overview>();
    public IQueryable<Company> Companies => Session.Query<Company>();
    public IQueryable<Profession> Professions => Session.Query<Profession>();
    public IQueryable<Car> Cars => Session.Query<Car>();
}
