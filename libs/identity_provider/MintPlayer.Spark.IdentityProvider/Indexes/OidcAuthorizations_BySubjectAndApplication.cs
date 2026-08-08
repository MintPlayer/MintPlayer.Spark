using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.IdentityProvider.Indexes;

public class OidcAuthorizations_BySubjectAndApplication : AbstractIndexCreationTask<OidcAuthorization>
{
    public OidcAuthorizations_BySubjectAndApplication()
    {
        Map = authorizations => from auth in authorizations
            select new
            {
                auth.Subject,
                auth.ApplicationId,
                auth.Status
            };
    }
}
