using MintPlayer.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Actions;

public partial class ArtistActions : DefaultPersistentObjectActions<Artist>, ISparkOwnsRowSecurity
{
    [Inject] private readonly IAsyncDocumentSession session;

    public string RowSecurityRationale => CatalogRowSecurity.Rationale;

    /// <summary>The bands a person plays in. Source: "Custom.Person_Artists".</summary>
    public Task<IQueryable<Artist>> Person_Artists(CustomQueryArgs args)
    {
        args.EnsureParent("Person");
        var personId = args.Parent!.Id;
        return Task.FromResult<IQueryable<Artist>>(session.Query<Artist>().Where(a => a.Members.Any(m => m.PersonId == personId)));
    }
}
