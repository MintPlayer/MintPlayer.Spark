using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.IdentityProvider.Indexes;

public class OidcTokens_ByReferenceId : AbstractIndexCreationTask<OidcToken>
{
    public OidcTokens_ByReferenceId()
    {
        Map = tokens => from token in tokens
            select new
            {
                token.ReferenceId,
                token.Type,
                token.Status
            };
    }
}
