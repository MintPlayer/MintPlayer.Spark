using MintPlayer.Spark.Abstractions.Authorization;
using DemoApp.Library.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;

namespace DemoApp.Actions;

public partial class CarActions : DefaultPersistentObjectActions<Car>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose — see PersonActions. Note the Fleet demo scopes its own Car type by driver, which is the shape a real application wants.";

    [Inject] private readonly ISparkSession session;

    /// <summary>
    /// The cars owned by the company whose detail page this sub-query is rendered on.
    /// <para>
    /// Source: <c>Custom.Company_Cars</c>. Plain LINQ over the collection, so it runs on any store provider; for the
    /// same query over a RavenDB index, see the Fleet and HR apps.
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
    public IQueryable<Car> Company_Cars(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return session.Query<Car>()
            .Where(c => c.Owner == args.Parent!.Id);
    }
}
