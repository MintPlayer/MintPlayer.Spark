using System.Text.Encodings.Web;
using System.Text.Json;

namespace MintPlayer.Spark.Gs1.Tests;

/// <summary>
/// Runs the GS1 parsing corpus (libs/gs1/corpus/gs1-corpus.json, built by tools/gs1/build-corpus.mjs) through
/// <see cref="Gs1Barcode"/>. Every case carries the verdict of GS1's own Barcode Syntax Engine: an input it accepts
/// must give the same elements in the same order, one it rejects must be rejected. ng-spark's
/// scanning/src/gs1-corpus.spec.ts holds the TypeScript twin to the same corpus.
/// </summary>
public class Gs1CorpusTests
{
    private static readonly Lazy<IReadOnlyDictionary<int, CorpusCase>> Corpus = new(Load);

    public static TheoryData<int> CaseIds()
    {
        var ids = new TheoryData<int>();
        foreach (var id in Corpus.Value.Keys) ids.Add(id);
        return ids;
    }

    [Fact]
    public void Corpus_covers_every_input_kind()
    {
        Corpus.Value.Count.Should().BeGreaterThanOrEqualTo(500);
        Corpus.Value.Values.Select(c => c.Kind).Distinct().Order().Should()
            .BeEquivalentTo(["bracketed", "digitalLink", "elementString", "scan"], o => o.WithStrictOrdering());
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Parser_agrees_with_the_GS1_engine(int id)
    {
        var c = Corpus.Value[id];
        var result = c.Kind == "digitalLink" ? Gs1Barcode.ParseDigitalLink(c.Input) : Gs1Barcode.Parse(c.Input);

        var expected = c.Valid ? Describe(c.Elements!.Select(e => (e[0], e[1]))) : "rejected";
        var actual = result.IsValid ? Describe(result.Elements.Select(e => (e.Ai, e.Value))) : "rejected";
        actual.Should().Be(expected, "the GS1 engine gives that for {0} {1} (case {2}, {3}; ours: {4})",
            c.Kind, Show(c.Input), c.Id, c.Source, result.IsValid ? "valid" : $"{result.Error} {result.ErrorAi}");
    }

    private static string Describe(IEnumerable<(string Ai, string Value)> elements)
        => "accepted " + string.Concat(elements.Select(e => $"({e.Ai}){e.Value}"));

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Show(string input) => JsonSerializer.Serialize(input, Readable).Replace("\\u001D", "<GS>");

    private static IReadOnlyDictionary<int, CorpusCase> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "gs1-corpus.json");
        var file = JsonSerializer.Deserialize<CorpusFile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"{path} holds no corpus");
        return file.Cases.ToDictionary(c => c.Id);
    }

    private sealed record CorpusFile(CorpusCase[] Cases);

    private sealed record CorpusCase(int Id, string Kind, string Input, string Source, bool Valid, string[][]? Elements, string? EngineError);
}
