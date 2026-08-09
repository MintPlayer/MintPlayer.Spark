using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.IdentityProvider.Indexes;

/// <summary>
/// Answers "which applications has this user granted?" — the one question the grant store could
/// not answer, because grant ids are a hash of (subject, application) and so are not scannable
/// by subject.
/// <para>
/// <b>For display only.</b> This index must never back an authorization decision. Index results
/// are eventually consistent, and this collection previously had an index removed for exactly
/// that reason: a grant revoked moments earlier still read back as valid and satisfied the
/// "already consented" check. Withdrawal therefore point-loads the grant by its derived id, and
/// so does every issuance check. The only thing this index is allowed to do is populate a list
/// on a page.
/// </para>
/// <para>
/// <c>Subject</c> is indexed <see cref="FieldIndexing.Exact"/> for the same reason
/// <c>OidcApplications_ByClientId</c> does it: a user id is an identifier, and the default
/// analyzer lowercases terms, so a query comparing verbatim input against lowercased terms
/// silently returns another user's rows or none at all.
/// </para>
/// </summary>
public class OidcAuthorizations_BySubject : AbstractIndexCreationTask<OidcAuthorization>
{
    public OidcAuthorizations_BySubject()
    {
        Map = authorizations => from auth in authorizations
            select new
            {
                auth.Subject,
            };

        Indexes.Add(x => x.Subject, FieldIndexing.Exact);
    }
}
