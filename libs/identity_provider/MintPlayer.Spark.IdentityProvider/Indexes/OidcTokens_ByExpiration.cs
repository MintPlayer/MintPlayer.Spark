using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.IdentityProvider.Indexes;

public class OidcTokens_ByExpiration : AbstractIndexCreationTask<OidcToken>
{
    public OidcTokens_ByExpiration()
    {
        Map = tokens => from token in tokens
            select new
            {
                token.ExpiresAt,
                token.Status
            };
    }
}
