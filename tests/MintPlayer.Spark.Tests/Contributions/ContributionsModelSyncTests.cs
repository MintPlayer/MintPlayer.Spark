using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Contributions;

/// <summary>
/// Contributions M5b: the generated types are real Spark persistent objects without app hand-work.
/// Synchronizing a context that exposes only the target writes model files for the generated
/// contribution and current types (satellites of the target), mints the contributions query once,
/// seeds the attribution renderer on the element's attribution attributes — and reaches a fixed point.
/// </summary>
public class ContributionsModelSyncTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "spark-contrib-sync-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment hostEnvironment = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog indexCatalog = Substitute.For<IIndexCatalog>();

    public ContributionsModelSyncTests()
    {
        Directory.CreateDirectory(contentRoot);
        hostEnvironment.ContentRootPath.Returns(contentRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelDir => Path.Combine(contentRoot, "App_Data", "Model");

    private void Synchronize() => new ModelSynchronizer(hostEnvironment, indexCatalog).SynchronizeModels(typeof(CoModelContext));

    private Dictionary<string, string> Snapshot() => Directory.GetFiles(ModelDir, "*.json")
        .ToDictionary(Path.GetFileName, File.ReadAllText, StringComparer.Ordinal)!;

    private static string Flat(Dictionary<string, string> files)
        => string.Join("\n", files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "\n" + f.Value));

    private JsonElement Read(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, file))).RootElement;

    private static JsonElement Attribute(JsonElement file, string name)
        => file.GetProperty("persistentObject").GetProperty("attributes").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);

    [Fact]
    public void Synchronize_writes_the_generated_types_and_their_query_and_reaches_a_fixed_point()
    {
        Synchronize();
        var first = Snapshot();
        Synchronize();
        var second = Snapshot();
        Synchronize();

        Flat(Snapshot()).Should().Be(Flat(second), "a second synchronize must change nothing");
        Flat(second).Should().Be(Flat(first), "the satellites' files are stable from the first run on");
        foreach (var file in new[] { "CoSong.json", "CoLyrics.json", "CoSongLyricsContribution.json", "CoSongLyricsCurrent.json" })
            first.ContainsKey(file).Should().BeTrue(file);

        var contribution = Read("CoSongLyricsContribution.json");
        contribution.GetProperty("persistentObject").GetProperty("clrType").GetString().Should().Be(typeof(CoSongLyricsContribution).FullName);
        var query = contribution.GetProperty("queries").EnumerateArray().Should().ContainSingle().Which;
        query.GetProperty("name").GetString().Should().Be(CoSongLyricsContributionMetadata.QueryName);
        query.GetProperty("source").GetString().Should().Be("Custom." + ContributionDescriptor.ContributionsQueryMethod);
        query.GetProperty("entityType").GetString().Should().Be("CoSongLyricsContribution");
        var sort = query.GetProperty("sortColumns").EnumerateArray().Single();
        sort.GetProperty("property").GetString().Should().Be("UpdatedAt");
        sort.GetProperty("direction").GetString().Should().Be("desc");
        Attribute(contribution, "ContributorName"); // Single(): present

        Read("CoSongLyricsCurrent.json").GetProperty("queries").GetArrayLength().Should().Be(0, "the current type gets no query");

        var lyrics = Read("CoLyrics.json");
        foreach (var name in new[] { "ContributorName", "UpdatedAt", "ContributionCount" })
        {
            var attribute = Attribute(lyrics, name);
            attribute.GetProperty("renderer").GetString().Should().Be(ContributionDescriptor.AttributionRenderingHint);
            var options = attribute.GetProperty("rendererOptions");
            options.GetProperty("contributionsQuery").GetString().Should().Be("cosonglyricscontributions");
            options.GetProperty("targetType").GetString().Should().Be("CoSong");
            options.GetProperty("slots").EnumerateArray().Select(s => s.GetString()).Should().Equal("Language", "Script");
        }
        Attribute(lyrics, "Text").TryGetProperty("renderer", out _).Should().BeFalse();
    }

    [Fact]
    public void The_contribution_type_diffs_its_text_by_default_and_leaves_the_contributor_id_to_the_application()
    {
        Synchronize();
        var contribution = Read("CoSongLyricsContribution.json");

        // #264, G-Q16: a library does not decide visibility, so ContributorId is created like any
        // other attribute, and how it is shown is the application's model file's call.
        var contributorId = Attribute(contribution, "ContributorId");
        contributorId.TryGetProperty("isVisible", out _).Should().BeFalse("isVisible was removed");
        contributorId.GetProperty("showedOn").GetString().Should().NotBe("None", "no library seed hides it");

        var text = Attribute(contribution, "Text");
        text.GetProperty("renderer").GetString().Should().Be(ContributionDescriptor.LineDiffRenderingHint);
        var options = text.GetProperty("rendererOptions");
        options.GetProperty("compareType").GetString().Should().Be("CoSong");
        options.GetProperty("compareAttribute").GetString().Should().Be("Lyrics");
        options.GetProperty("compareRowAttribute").GetString().Should().Be("Text");
        Attribute(contribution, "Language").TryGetProperty("renderer", out _).Should().BeFalse("only value attributes are diffed");

        // The application's choice survives a re-synchronize.
        AuthorShowedOnNone("CoSongLyricsContribution.json", "ContributorId");
        Synchronize();
        Attribute(Read("CoSongLyricsContribution.json"), "ContributorId").GetProperty("showedOn").GetString().Should().Be("None");
    }

    [Fact]
    public void The_generated_row_key_is_an_ordinary_attribute_the_application_shapes()
    {
        Synchronize();

        // #264, G-Q16: no library seed; identity travels as po.Id, so the application decides.
        var key = Attribute(Read("CoLyrics.json"), ContributionDescriptor.RowKeyName);
        key.TryGetProperty("isVisible", out _).Should().BeFalse("isVisible was removed");
        key.GetProperty("showedOn").GetString().Should().NotBe("None", "no library seed hides it");

        AuthorShowedOnNone("CoLyrics.json", ContributionDescriptor.RowKeyName);
        Synchronize();

        Attribute(Read("CoLyrics.json"), ContributionDescriptor.RowKeyName).GetProperty("showedOn").GetString()
            .Should().Be("None", "an authored showedOn: None is never healed (G-Q4)");
    }

    private void AuthorShowedOnNone(string file, string attribute)
    {
        var path = Path.Combine(ModelDir, file);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var authored = json["persistentObject"]!["attributes"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == attribute)!;
        authored["showedOn"] = "None";
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void The_line_diff_options_derive_the_target_id_and_the_row_key_from_the_contribution_id()
    {
        var options = ContributionDescriptor.LineDiffRendererOptions(CoSongLyricsContributionMetadata.Instance, "Text");
        var id = CoSongLyricsContribution.GetId("CoSongs/1", "en", "Latn", "MintPlayerUsers/abc");

        System.Text.RegularExpressions.Regex.Replace(id, (string)options["compareIdPattern"], (string)options["compareIdReplacement"])
            .Should().Be("CoSongs/1");
        System.Text.RegularExpressions.Regex.Replace(id, (string)options["compareRowKeyPattern"], (string)options["compareRowKeyReplacement"])
            .Should().Be("en/Latn");
    }

    [Fact]
    public void An_authored_renderer_and_a_renamed_query_survive_synchronize()
    {
        Synchronize();
        var path = Path.Combine(ModelDir, "CoLyrics.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"contributionAttribution\"", "\"myRenderer\""));
        var contributionPath = Path.Combine(ModelDir, "CoSongLyricsContribution.json");
        var queryId = Read("CoSongLyricsContribution.json").GetProperty("queries")[0].GetProperty("id").GetString();

        Synchronize();

        File.ReadAllText(path).Should().Contain("\"myRenderer\"").And.NotContain("\"contributionAttribution\"", "a seed never overwrites");
        Read("CoSongLyricsContribution.json").GetProperty("queries")[0].GetProperty("id").GetString().Should().Be(queryId);
        File.Exists(contributionPath).Should().BeTrue();
    }

    [Fact]
    public void The_model_hash_covers_the_generated_types()
    {
        var entities = ModelShapeDiscovery.Discover(typeof(CoModelContext), indexCatalog).Select(t => t.Type).ToList();

        foreach (var type in new[] { typeof(CoSong), typeof(CoLyrics), typeof(CoSongLyricsContribution), typeof(CoSongLyricsCurrent) })
            entities.Should().Contain(type);
        ModelShapeDiscovery.Discover(typeof(CoUnrelatedContext), indexCatalog).Select(t => t.Type)
            .Should().NotContain(typeof(CoSongLyricsContribution), "satellites are keyed by their target");
    }
}

public sealed class CoModelContext : SparkContext
{
    public IRavenQueryable<CoSong> CoSongs => Session.Query<CoSong>();
}

public sealed class CoUnrelatedContext : SparkContext
{
    public IRavenQueryable<CoIdentity> CoIdentities => Session.Query<CoIdentity>();
}
