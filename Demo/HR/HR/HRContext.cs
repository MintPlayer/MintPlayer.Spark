using HR.Entities;
using HR.Indexes;
using HR.Replicated;
using MintPlayer.Spark;

namespace HR;

public class HRContext : SparkContext
{
    public IQueryable<Person> People => Session.Query<Person>();
    public IQueryable<VPerson> VPeople => Session.Query<VPerson>(nameof(People_Overview));
    public IQueryable<Company> Companies => Session.Query<Company>();
    public IQueryable<Profession> Professions => Session.Query<Profession>();
    public IQueryable<Car> Cars => Session.Query<Car>();
}
