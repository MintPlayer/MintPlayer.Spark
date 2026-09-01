using DemoApp.Indexes;
using DemoApp.Library.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace DemoApp.Actions;

public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// The cars owned by the company whose detail page this sub-query is rendered on.
    /// <para>
    /// Source: <c>Custom.Company_Cars</c>, bound to the <c>Company_Cars</c> index through the
    /// query's <c>indexName</c> — which is why it can filter on <c>CompanyId</c>, a field
    /// <c>VCar</c> (the list screen's projection) does not carry.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <c>EnsureParent</c> is not politeness: without a parent this would return every car in the
    /// database, and a sub-query that silently ignores its container is the kind of bug that only
    /// shows up as "why does this company own 400 cars".
    /// <para>
    /// Row security composes onto this for free — the framework applies the type's row filter to
    /// every query surface, so nothing here has to repeat it. Contrast a <b>composed</b> query
    /// (one whose type has no <c>clrType</c>), where there is no document to judge and the method
    /// itself is the only filter; see the composed-queries section of the query guide.
    /// </para>
    /// </remarks>
    public IRavenQueryable<VCompanyCar> Company_Cars(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return session.Query<VCompanyCar, Company_Cars>()
            .Where(c => c.CompanyId == args.Parent!.Id);
    }
}
