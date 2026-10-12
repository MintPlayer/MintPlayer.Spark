using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Authorization.Identity;
using QnA.Entities;
using Raven.Client.Documents;

namespace QnA.Actions;

/// <summary>
/// The row rule behind <c>Read/SparkUser</c> (security.json): a user is readable only while they author a
/// published question or a live answer — the people whose name a visitor already sees on a post.
/// </summary>
/// <remarks>
/// <para>
/// Since MCP PRD D19 (owner decision 2026-10-12) a reference attribute is shown only to a caller who may
/// <c>Read</c> its target type, so without this grant every <c>AuthorId</c>, <c>CreatedBy</c>,
/// <c>ModifiedBy</c> and <c>DeletedBy</c> vanished from questions and answers. The grant goes to the
/// same groups that read posts (anonymous included); this rule keeps it from publishing every account:
/// a user who wrote nothing visible — a moderator who only edited, someone with only drafts or only
/// deleted posts — stays unreadable, and a reference to them renders the placeholder.
/// </para>
/// <para>
/// Nothing beyond the name is exposed: SparkUser's model declares only <c>UserName</c> (the library's
/// <c>SparkUser.json</c>), so credential fields, e-mail and phone cannot reach the wire. And the pairing
/// id → name was already public on every post, so loading an author by id discloses nothing new.
/// </para>
/// <para>
/// Per row, not a filter: "authored something" is a question about other collections that a
/// <c>SparkUser</c> predicate cannot express. No query lists users, so this only runs for a load and for
/// the reference labels of a rendered page; the answer is cached per user for the request, and the
/// lookups use their own session so a page of authors does not spend the request session's budget.
/// </para>
/// </remarks>
public partial class SparkUserActions : DefaultPersistentObjectActions<SparkUser>
{
    [Inject] private readonly IDocumentStore store;

    private readonly Dictionary<string, bool> authors = new(StringComparer.OrdinalIgnoreCase);

    public override async Task<bool> IsAllowedAsync(string action, SparkUser entity)
    {
        if (action is not ("Read" or "Query") || string.IsNullOrEmpty(entity.Id))
            return false;

        var id = entity.Id;
        if (authors.TryGetValue(id, out var known))
            return known;

        using var lookup = store.OpenAsyncSession();
        // `!= true`, not `== false`: an absent field matches neither `== false` nor `!x` in RavenDB.
        var authored = await lookup.Query<Question>().AnyAsync(q => q.AuthorId == id && q.IsDeleted != true && q.IsDraft != true)
            || await lookup.Query<Answer>().AnyAsync(a => a.AuthorId == id && a.IsDeleted != true);

        authors[id] = authored;
        return authored;
    }
}
