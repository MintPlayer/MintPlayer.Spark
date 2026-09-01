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
    /// <para>
    /// Declared <c>Task&lt;IQueryable&lt;T&gt;&gt;</c> rather than <c>Task&lt;IRavenQueryable&lt;T&gt;&gt;</c>
    /// on purpose. The declared type is weaker than what the method actually returns, which is the
    /// common idiom and the case the executor has to get right: capabilities are inferred from the
    /// object, so this still gets index projection, includes and search pushdown (#294). Inferring
    /// from the signature would silently downgrade it to an in-memory queryable.
    /// </para>
    /// Source: "Custom.Company_People"
    /// </summary>
    public async Task<IQueryable<VPerson>> Company_People(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return await Task.FromResult<IQueryable<VPerson>>(session.Query<VPerson, People_Overview>()
            .Where(p => p.Company == args.Parent!.Id));
    }
}
