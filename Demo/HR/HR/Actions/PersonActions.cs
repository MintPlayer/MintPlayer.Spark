using HR.Indexes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace HR.Actions;

public partial class PersonActions : DefaultPersistentObjectActions<Entities.Person>
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// Custom query: returns people belonging to a specific company.
    /// Source: "Custom.Company_People"
    /// </summary>
    public IRavenQueryable<VPerson> Company_People(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return session.Query<VPerson, People_Overview>()
            .Where(p => p.Company == args.Parent!.Id);
    }
}
