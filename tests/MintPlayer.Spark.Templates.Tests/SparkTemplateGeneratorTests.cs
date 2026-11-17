using System.Text.Json.Nodes;
using MintPlayer.Spark.Templates.Build;

namespace MintPlayer.Spark.Templates.Tests;

/// <summary>The pack-time generator, run in memory on the real manifest and template.</summary>
public class SparkTemplateGeneratorTests
{
    private static readonly FeatureManifest Manifest = TemplateRepository.Manifest();
    private static readonly TemplateFiles Generated = SparkTemplateGenerator.Generate(Manifest, TemplateRepository.HandWritten());

    [Fact]
    public void Every_generated_feature_gets_a_parameter_and_a_condition()
    {
        var symbols = TemplateRepository.Symbols(Generated.TemplateJson);
        var missing = Manifest.Features.Where(f => !f.IsHandWritten)
            .Where(f => symbols[f.Id]?["type"]?.GetValue<string>() != "parameter"
                        || symbols[f.Condition]?["type"]?.GetValue<string>() != "computed")
            .Select(f => f.Id)
            .ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void A_prerequisite_is_switched_on_by_its_dependents()
    {
        var symbols = TemplateRepository.Symbols(Generated.TemplateJson);

        symbols["UseMessaging"]!["value"]!.GetValue<string>()
            .Should().Be("(IsRaven && ((Messaging && true) || (IsRaven && (Mail && true))))");
        symbols["UseMigrations"]!["value"]!.GetValue<string>()
            .Should().Be("(IsRaven && ((Migrations && true) || HasAuth))");
    }

    [Fact]
    public void A_feature_without_constraints_has_a_plain_condition()
        => TemplateRepository.Symbols(Generated.TemplateJson)["UseLogging"]!["value"]!.GetValue<string>()
            .Should().Be("((Logging && true))");

    [Fact]
    public void A_store_bound_feature_is_disabled_for_the_other_store()
    {
        var history = TemplateRepository.Symbols(Generated.TemplateJson)["History"]!;

        history["isEnabled"]!.GetValue<string>().Should().Be("Store == \"RavenDB\"");
        history["description"]!.GetValue<string>()
            .Should().Be("Revisions, audit stamping and revert (MintPlayer.Spark.History). Needs RavenDB.");
    }

    [Fact]
    public void Every_generated_feature_has_a_cli_flag_and_shows_in_visual_studio()
    {
        var cli = JsonNode.Parse(Generated.CliHostJson)!["symbolInfo"]!.AsObject();
        var ide = JsonNode.Parse(Generated.IdeHostJson)!["symbolInfo"]!.AsArray()
            .Select(s => s!["id"]!.GetValue<string>()).ToList();

        var missing = Manifest.Features.Where(f => !f.IsHandWritten)
            .Where(f => cli[f.Id]?["longName"]?.GetValue<string>() != f.Flag || !ide.Contains(f.Id))
            .Select(f => f.Id)
            .ToList();

        missing.Should().BeEmpty();
        ide.Last().Should().Be("skipRestore", "hidden symbols stay last");
    }

    [Fact]
    public void Every_feature_package_is_referenced_under_its_condition()
    {
        var csproj = Generated.HostCsproj.Replace("\r\n", "\n");
        csproj.Should().NotContain(SparkTemplateGenerator.PackageReferencesMarker);
        foreach (var feature in Manifest.Features)
            csproj.Should().Contain($"<!--#if ({feature.Condition}) -->\n\t\t<PackageReference Include=\"{feature.PackageId}\" />\n<!--#endif -->");
    }

    [Fact]
    public void A_prerequisite_cycle_is_refused()
    {
        var manifest = new FeatureManifest
        {
            Features =
            [
                Feature("A", requires: ["B"]),
                Feature("B", requires: ["A"]),
            ],
        };

        Action act = () => SparkTemplateGenerator.Generate(manifest, TemplateRepository.HandWritten());
        act.Should().Throw<InvalidOperationException>().Where(e => e.Message.Contains("A requires itself"));
    }

    [Fact]
    public void A_prerequisite_bound_to_a_store_its_dependent_lacks_is_refused()
    {
        var manifest = new FeatureManifest
        {
            Features =
            [
                Feature("Bus", store: "RavenDB"),
                Feature("Anywhere", requires: ["Bus"]),
            ],
        };

        Action act = () => SparkTemplateGenerator.Generate(manifest, TemplateRepository.HandWritten());
        act.Should().Throw<InvalidOperationException>().Where(e => e.Message.Contains("Anywhere requires Bus, which needs RavenDB"));
    }

    [Fact]
    public void A_feature_the_hand_written_template_already_defines_is_refused()
    {
        var manifest = new FeatureManifest { Features = [Feature("Store")] };

        Action act = () => SparkTemplateGenerator.Generate(manifest, TemplateRepository.HandWritten());
        act.Should().Throw<InvalidOperationException>().Where(e => e.Message.Contains("already defines 'Store'"));
    }

    private static Feature Feature(string id, string[]? requires = null, string? store = null) => new()
    {
        Id = id,
        Flag = id.ToLowerInvariant(),
        DisplayName = id,
        Description = id,
        Project = $"x/{id}/{id}.csproj",
        Requires = [.. requires ?? []],
        Store = store,
    };
}
