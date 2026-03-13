using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.IdentityProvider.Indexes;

public class OidcApplications_ByClientId : AbstractIndexCreationTask<OidcApplication>
{
    public OidcApplications_ByClientId()
    {
        Map = applications => from app in applications
            select new
            {
                app.ClientId,
                app.Enabled
            };
    }
}
