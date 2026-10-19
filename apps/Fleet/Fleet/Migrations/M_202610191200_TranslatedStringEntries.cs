using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace Fleet.Migrations;

/// <summary>
/// Moves Fleet's stored texts to the one <c>TranslatedString</c> shape,
/// <c>{"Entries":[{"Tag":"en","Value":"…"}]}</c> (commerce X17):
/// <list type="bullet">
/// <item><b>S24 M2</b>: <c>Cars.Description</c>, from the Newtonsoft POCO form
/// <c>{"Translations":{"en":"…"},"Key":null}</c>. The <c>Cars_Overview</c> index maps the new path; it is
/// deployed before the migrations run and re-indexes the rewritten documents.</item>
/// <item><b>S24 M3</b>: the dynamic lookup values (<c>LookupReferences/CarBrand</c>), whose
/// <c>Values[i].Translations</c> dictionary becomes <c>Values[i].Values</c>, the same shape.</item>
/// </list>
/// Idempotent: only the old shape is rewritten. Registered by hand in Program.cs: Fleet runs no
/// library migrations.
/// </summary>
/// <remarks>
/// The collection and property names are strings frozen at the shape the database had, as in every
/// migration here; the scripts are constants (no splicing).
/// </remarks>
public partial class M_202610191200_TranslatedStringEntries : ISparkMigration
{
    public static long Version => 202610191200;
    public static string? Description => "Cars.Description and dynamic lookup values to the TranslatedString entries shape (X17)";

    [Inject] private readonly IDocumentStore store;

    /// <summary>The tag in canonical case, and a <c>{Translations}</c> dictionary to <c>{Entries}</c>.</summary>
    private const string Functions = """
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
            function entriesOf(translations) {
                var list = [];
                for (var tag in translations) {
                    var text = translations[tag];
                    if (text !== null && text !== undefined) list.push({ Tag: canon(tag), Value: text });
                }
                return { Entries: list };
            }
        """;

    private const string CarsScript = "from Cars as d update {" + Functions + """
            if (d.Description && typeof d.Description === 'object' && !d.Description.Entries && d.Description.Translations)
                d.Description = entriesOf(d.Description.Translations);
        }
        """;

    private const string DynamicLookupsScript = "from DynamicLookupReferenceDocuments as d update {" + Functions + """
            if (d.Values) {
                for (var v = 0; v < d.Values.length; v++) {
                    var value = d.Values[v];
                    if (value.Translations && !value.Values) {
                        value.Values = entriesOf(value.Translations);
                        delete value.Translations;
                    }
                }
            }
        }
        """;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await PatchAsync(CarsScript, cancellationToken);
        await PatchAsync(DynamicLookupsScript, cancellationToken);
    }

    private async Task PatchAsync(string script, CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = script }, new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);

        // Wait, so a throw aborts startup and the migration is retried on the next start rather than
        // being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
