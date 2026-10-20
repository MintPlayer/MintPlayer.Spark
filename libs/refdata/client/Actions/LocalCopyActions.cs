using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.ReferenceData.Client;

// The read-only local copies (PRD §4.7, plan R10): countries, places and postal codes, declared once, in this package's
// library layer (alias reference-data-client). Only the release sync (a consumer) or the import and the seed (an
// instance, which references this package) write them: no New/Edit/Delete right ships. An instance and a consumer
// share these declarations, so a host that is both never composes the same type from two layers (SPARK057).

public partial class RefCountryActions : DefaultPersistentObjectActions<RefCountry>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<RefCountry> RefCountries() => session.Query<RefCountry>();
}

public partial class RefPlaceActions : DefaultPersistentObjectActions<RefPlace>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<RefPlace> RefPlaces() => session.Query<RefPlace>();
}

public partial class RefPostalCodeActions : DefaultPersistentObjectActions<RefPostalCode>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<RefPostalCode> RefPostalCodes() => session.Query<RefPostalCode>();
}
