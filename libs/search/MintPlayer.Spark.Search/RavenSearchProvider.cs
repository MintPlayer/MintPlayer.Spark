using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Search;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Search;

/// <summary>
/// The default provider (<c>docs/spark_search_PRD.md</c> D5): the app's generated <c>Spark_Search</c> multi-map index
/// (<see cref="SparkSearchIndex"/>). Every word must match, the last one as a prefix while typing; a fuzzy type also
/// matches a word within a small edit distance of its <c>Terms</c> (D7, which is why the index uses Lucene). Results come
/// best first. Every value goes in as a query argument through the document-query builder, never spliced into RQL.
/// </summary>
internal sealed partial class RavenSearchProvider : ISparkSearchProvider
{
    /// <summary>A word shorter than this never matches fuzzily: two letters within one edit match almost anything.</summary>
    internal const int MinFuzzyWordLength = 4;

    /// <summary>Lucene's similarity for a fuzzy term (0.6 ≈ one edit for a five-letter word).</summary>
    internal const decimal FuzzySimilarity = 0.6m;

    [Inject] private readonly IDocumentStore store;

    public async Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(SparkSearchRequest request, CancellationToken cancellationToken)
    {
        var words = SearchTerms.Words(request.Term);
        if (words.Count == 0 || request.EntityTypes.Count == 0)
            return [];

        using var session = store.OpenAsyncSession();
        var query = Base(session, request.EntityTypes).AndAlso().OpenSubclause();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
                query = query.AndAlso();
            var word = words[i];
            var prefix = request.Prefix && i == words.Count - 1;
            query = query.OpenSubclause().Search(SparkSearchIndex.Text, prefix ? word + "*" : word);
            if (request.FuzzyTypes.Count > 0 && word.Length >= MinFuzzyWordLength)
            {
                query = query.OrElse().OpenSubclause()
                    .WhereIn(SparkSearchIndex.Type, request.FuzzyTypes.ToArray())
                    .AndAlso().WhereEquals(SparkSearchIndex.Terms, word).Fuzzy(FuzzySimilarity)
                    .CloseSubclause();
            }
            query = query.CloseSubclause();
        }
        query = query.CloseSubclause();

        return await RunAsync(query, request.Take, cancellationToken);
    }

    public async Task<IReadOnlyList<SparkSearchCandidate>> FindByLabelAsync(string label, IReadOnlyCollection<string> entityTypes, CancellationToken cancellationToken)
    {
        var words = SearchTerms.Words(label);
        if (words.Count == 0 || entityTypes.Count == 0)
            return [];

        // Every word of the label, exactly: the core compares the rendered labels, so this only has to be a superset.
        using var session = store.OpenAsyncSession();
        var query = Base(session, entityTypes);
        foreach (var word in words)
            query = query.AndAlso().Search(SparkSearchIndex.Text, word);

        return await RunAsync(query, FindByLabelTake, cancellationToken);
    }

    /// <summary>The most namesakes a label lookup returns: beyond this, numbering them is meaningless anyway.</summary>
    internal const int FindByLabelTake = 100;

    private static IAsyncDocumentQuery<SparkSearchEntry> Base(IAsyncDocumentSession session, IReadOnlyCollection<string> types)
        => session.Advanced.AsyncDocumentQuery<SparkSearchEntry>(SparkSearchIndex.Name)
            .WhereIn(SparkSearchIndex.Type, types.ToArray())
            .AndAlso().WhereEquals(SparkSearchIndex.IsDeleted, false);

    private static async Task<IReadOnlyList<SparkSearchCandidate>> RunAsync(IAsyncDocumentQuery<SparkSearchEntry> query, int take, CancellationToken cancellationToken)
    {
        // Type is a stored index field (the document itself has none), so the projection must read it from the index.
        var rows = await query
            .OrderByScore()
            .Take(take)
            .SelectFields<CandidateRow>(Raven.Client.Documents.Queries.ProjectionBehavior.FromIndexOrThrow, nameof(CandidateRow.Type))
            .ToListAsync(cancellationToken);
        return rows
            .Where(r => r.Id is not null && r.Type is not null)
            .Select(r => new SparkSearchCandidate(r.Type!, r.Id!))
            .ToList();
    }

    private sealed class CandidateRow
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
    }
}
