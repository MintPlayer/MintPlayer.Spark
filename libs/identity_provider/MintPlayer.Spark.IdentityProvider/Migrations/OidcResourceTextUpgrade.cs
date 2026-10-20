using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// Rewrites the <c>TranslatedString</c> texts of stored <c>OidcResources</c> into the X17 shape
/// (commerce X17, S24 M1): <c>DisplayName</c>, <c>Description</c> and every <c>Scopes[i].DisplayName</c> /
/// <c>.Description</c> go from the Newtonsoft POCO form <c>{"Translations":{"en":"…"},"Key":null}</c> to
/// <c>{"Entries":[{"Tag":"en","Value":"…"}]}</c>, tags in canonical case.
/// <para>
/// Idempotent: a value that already has <c>Entries</c>, or has no <c>Translations</c>, is left as it is,
/// and RavenDB writes no document the script did not change.
/// </para>
/// <para>
/// Two callers. <see cref="M_202610191200_OidcResourceTextEntries"/> is the migration. The MCP seeding
/// (<c>OidcMcpResources.InitializeAsync</c>) runs it first as well, because it loads <c>OidcResources</c>
/// from a startup hook that can run before the migrations: CodeCoverage registers
/// <c>AddIdentityProvider</c> before <c>AddMigrations</c>, and Fleet hosts the provider without a
/// migration runner. Without it, the first load of an old document would fail startup.
/// </para>
/// <para>
/// The script is a constant: no identifier or value is spliced into it (repo rule).
/// </para>
/// </summary>
internal static class OidcResourceTextUpgrade
{
    private const string Script = """
        from OidcResources as d update {
            function isLetters(p) {
                for (var k = 0; k < p.length; k++) {
                    var c = p.charCodeAt(k);
                    if (!((c >= 65 && c <= 90) || (c >= 97 && c <= 122))) return false;
                }
                return p.length > 0;
            }
            function canon(tag) {
                var parts = tag.split('-');
                var afterSingleton = false;
                for (var i = 0; i < parts.length; i++) {
                    var p = parts[i];
                    var letters = isLetters(p);
                    if (i > 0 && !afterSingleton && p.length === 4 && letters) parts[i] = p.charAt(0).toUpperCase() + p.substring(1).toLowerCase();
                    else if (i > 0 && !afterSingleton && p.length === 2 && letters) parts[i] = p.toUpperCase();
                    else parts[i] = p.toLowerCase();
                    if (p.length === 1) afterSingleton = true;
                }
                return parts.join('-');
            }
            function entries(v) {
                if (v === null || v === undefined || typeof v !== 'object' || v.Entries || !v.Translations) return v;
                var list = [];
                for (var tag in v.Translations) {
                    var text = v.Translations[tag];
                    if (text !== null && text !== undefined) list.push({ Tag: canon(tag), Value: text });
                }
                return { Entries: list };
            }
            if (d.DisplayName) d.DisplayName = entries(d.DisplayName);
            if (d.Description) d.Description = entries(d.Description);
            if (d.Scopes) {
                for (var s = 0; s < d.Scopes.length; s++) {
                    if (d.Scopes[s].DisplayName) d.Scopes[s].DisplayName = entries(d.Scopes[s].DisplayName);
                    if (d.Scopes[s].Description) d.Scopes[s].Description = entries(d.Scopes[s].Description);
                }
            }
        }
        """;

    public static async Task RunAsync(IDocumentStore store, CancellationToken cancellationToken = default)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = Script }, new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);

        // Wait, so a throw aborts startup and the migration is retried on the next start rather than
        // being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
