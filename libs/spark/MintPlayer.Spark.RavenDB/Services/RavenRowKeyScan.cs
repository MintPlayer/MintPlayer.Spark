using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Services;

/// <summary>RavenDB's <see cref="ISparkStoreRowKeyScan"/>: a streamed collection scan with a JavaScript projection.</summary>
[Register(typeof(ISparkStoreRowKeyScan), ServiceLifetime.Singleton)]
internal sealed partial class RavenRowKeyScan : ISparkStoreRowKeyScan
{
    [Inject] private readonly IDocumentStore store;

    /// <summary>The projection of one scanned document: how many of its rows on the path lack a key.</summary>
    private sealed class KeylessCount
    {
        public int Keyless { get; set; }
    }


    /// <summary>
    /// Counts the rows at a dot-separated <c>path</c> whose <c>key</c> is absent, null or "". Every
    /// array met on the way is flattened, so <c>Sessions.Reports</c> reaches the reports of every
    /// session. A null element is not a row.
    /// </summary>
    private const string KeylessRowsFunction = """
        declare function keylessRows(doc, path, key) {
            var nodes = [doc];
            var segments = path.split('.');
            for (var i = 0; i < segments.length; i++) {
                var next = [];
                for (var j = 0; j < nodes.length; j++) {
                    var value = nodes[j][segments[i]];
                    if (value === null || value === undefined) continue;
                    if (Array.isArray(value)) {
                        for (var k = 0; k < value.length; k++) {
                            if (value[k] !== null && value[k] !== undefined) next.push(value[k]);
                        }
                    } else {
                        next.push(value);
                    }
                }
                nodes = next;
            }
            var keyless = 0;
            for (var j = 0; j < nodes.length; j++) {
                var id = nodes[j][key];
                if (id === null || id === undefined || id === '') keyless++;
            }
            return keyless;
        }

        """;

    public async Task<int> CountDocumentsWithKeylessRowsAsync(string collection, string path, string keyProperty, CancellationToken cancellationToken)
    {
        // ⚠️ A collection SCAN, not a `where` query (D-O18). `where Lines[].Id == null` creates an auto index per root
        // collection on every application at startup — indexes belong to the application — and the request write path
        // then waits on those auto indexes after every write to the collection. A collection query without `where` uses
        // no index, and the JavaScript projection sends back one number per document instead of the document.
        //
        // Only the collection is spliced, because RQL takes no parameter for it; it is validated. Path and key are
        // parameters.
        var rql = KeylessRowsFunction + $"from '{RqlIdentifier.Collection(collection)}' as d select {{ Keyless: keylessRows(d, $path, $key) }}";

        using var session = store.OpenAsyncSession();
        var query = session.Advanced.AsyncRawQuery<KeylessCount>(rql)
            .AddParameter("path", path)
            .AddParameter("key", keyProperty);

        var documents = 0;
        await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
        while (await stream.MoveNextAsync())
        {
            if (stream.Current.Document?.Keyless > 0)
                documents++;
        }
        return documents;
    }
}
