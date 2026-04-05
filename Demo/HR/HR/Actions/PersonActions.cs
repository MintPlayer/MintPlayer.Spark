using HR.Indexes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;

namespace HR.Actions;

public partial class PersonActions : DefaultPersistentObjectActions<Entities.Person>
{
    /// <summary>
    /// Custom query: returns people belonging to a specific company.
    /// Source: "Custom.Company_People"
    /// </summary>
    public IQueryable<VPerson> Company_People(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return args.Session.Query<VPerson>(nameof(People_Overview))
            .Where(p => p.Company == args.Parent!.Id);
    }
}
