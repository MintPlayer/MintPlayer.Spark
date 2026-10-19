using MintPlayer.Spark;
using Raven.Client.Documents.Linq;
using ReferenceData.Library.Entities;

namespace ReferenceData;

/// <summary>
/// The app's own entities: only the consumer page's venues. The reference types (RefPlace, RefPostalCode, …) come from
/// the reference-data library's layer, with their admin queries.
/// </summary>
public partial class ReferenceDataContext : SparkContext
{
    public IRavenQueryable<Venue> Venues => Session.Query<Venue>();
}
