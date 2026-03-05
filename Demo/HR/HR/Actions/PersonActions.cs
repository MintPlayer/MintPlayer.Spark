using HR.Entities;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;

namespace HR.Actions;

public partial class PersonActions : DefaultPersistentObjectActions<Person>
{
    /// <summary>
    /// Custom query: returns people belonging to a specific company.
    /// Source: "Custom.Company_People"
    /// </summary>
    public IRavenQueryable<Person> Company_People(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return args.Session.Query<Person>()
            .Where(p => p.Company == args.Parent!.Id);
    }
}
