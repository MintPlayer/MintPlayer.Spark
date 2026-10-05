using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;

namespace MintPlayer.Spark.Authorization.Migrations;

/// <summary>
/// Gives every stored <see cref="SparkUser"/> whose user name contains <c>@</c> a generated handle,
/// <c>user-</c> and six hex digits (#264, G-Q22).
///
/// Registration used to set <c>UserName = email</c>, and the user name is what other users see: a
/// reference label (<c>{UserName}</c> breadcrumb), a history author, a contributor. Since G-Q22 a user
/// name never contains <c>@</c> (<see cref="SparkUserNameValidator{TUser}"/>), so every such account is
/// renamed once; the owner can pick a better handle on the account page. Sign-in by email is unaffected.
///
/// <c>NormalizedUserName</c> is rewritten with it (Identity's default normalizer upper-cases; the
/// handle is ASCII). A generated handle is checked against every user name in the collection, so it is
/// unique. Documents are patched, not stored, so an application subclass and the protected secrets on
/// the document are left as they are. A second run finds nothing to do. Shipped by the package and
/// registered by each application's generated <c>AddMigrations()</c>.
/// </summary>
public partial class M_202610051200_UserNamesAreNotEmails : ISparkMigration
{
    public static long Version => 202610051200;
    public static string? Description => "User names are public handles: replace email-shaped user names with a generated handle";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var collection = store.Conventions.FindCollectionName(typeof(SparkUser));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emailShaped = new List<string>();

        using (var session = store.OpenAsyncSession())
        {
            // A collection scan, no index: nothing to wait for and nothing stale.
            var query = session.Advanced.AsyncRawQuery<UserNameRow>($"from '{collection}' select id() as Id, UserName");
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
            {
                var row = stream.Current.Document;
                if (string.IsNullOrEmpty(row.UserName))
                    continue;

                taken.Add(row.UserName);
                if (row.UserName.Contains('@'))
                    emailShaped.Add(row.Id ?? stream.Current.Id);
            }
        }

        if (emailShaped.Count == 0)
            return;

        using (var session = store.OpenAsyncSession())
        {
            foreach (var id in emailShaped)
            {
                string handle;
                do handle = SparkUserNames.NewHandle();
                while (!taken.Add(handle));

                session.Advanced.Defer(new PatchCommandData(id, null, new PatchRequest
                {
                    // Re-checked on the server: a user renamed between the scan and this patch keeps their name.
                    Script = "if (this.UserName && this.UserName.indexOf('@') >= 0) { this.UserName = $name; this.NormalizedUserName = $normalized; }",
                    Values =
                    {
                        ["name"] = handle,
                        ["normalized"] = handle.ToUpperInvariant(),
                    },
                }));
            }

            // One transaction: a throw aborts startup and the migration is retried on the next start
            // rather than being marked done half-applied.
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class UserNameRow
    {
        public string? Id { get; set; }
        public string? UserName { get; set; }
    }
}
