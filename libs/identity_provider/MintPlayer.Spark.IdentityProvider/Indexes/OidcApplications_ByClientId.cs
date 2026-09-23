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

        // A client id is an identifier, not text: it must match verbatim or not at all.
        //
        // RavenDB's default string indexing lowercases the term, which had two consequences in
        // sequence. Originally the query lowercased too, so they agreed and `acmeapp` resolved
        // the application registered as `AcmeApp` — impersonation by casing. Adding `exact: true`
        // to the query alone made it worse, not better: the query then compared a verbatim input
        // against lowercased terms, so the *correctly* cased id stopped matching while the
        // lowercased one still did. The index side has to change with the query side.
        // Declared by name, not by lambda: the lambda-keyed dictionary is invisible to the
        // string-keyed guard the generator emits, so the two forms collide as an
        // IndexCompilationException at startup the moment this index gains a projection. The string
        // overload is also the only one that can name a computed or companion field.
        Index(nameof(OidcApplication.ClientId), FieldIndexing.Exact);
    }
}
