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
        var missing = Manifest.Features.Where(f => f.HasParameter)
            .Where(f => symbols[f.Id]?["type"]?.GetValue<string>() != "parameter"
                        || symbols[f.Condition]?["type"]?.GetValue<string>() != "computed")
            .Select(f => f.Id)
            .ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void A_prerequisite_is_switched_on_by_its_dependents()
    {
        var manifest = new FeatureManifest
        {
            Features =
            [
                Feature("Bus", store: "RavenDB"),
                Feature("Mail", requires: ["Bus"], store: "RavenDB"),
            ],
        };

        Symbols(manifest)["UseBus"]!["value"]!.GetValue<string>()
            .Should().Be("(IsRaven && ((Bus && true) || (IsRaven && (Mail && true))))");
    }

    [Fact]
    public void A_runtime_requirement_switches_on_like_a_reference()
        => TemplateRepository.Symbols(Generated.TemplateJson)["UseMessaging"]!["value"]!.GetValue<string>()
            .Should().Contain("(IsRaven && (Replication && true))", "Replication alsoRequires Messaging");

    [Fact]
    public void A_feature_chosen_by_a_hand_written_parameter_gets_only_its_condition()
    {
        var manifest = new FeatureManifest
        {
            Features =
            [
                Feature("Mig"),
                Feature("Auth", requires: ["Mig"], symbol: "HasAuth", selectedWhen: "AuthMode == \"oidc\""),
                Feature("Dash", requires: ["Auth"]),
            ],
        };

        var symbols = Symbols(manifest);
        symbols.ContainsKey("Auth").Should().BeFalse("AuthMode chooses it");
        symbols["HasAuth"]!["value"]!.GetValue<string>()
            .Should().Be("(((AuthMode == \"oidc\") || ((Dash && true))))");
        symbols["UseMig"]!["value"]!.GetValue<string>()
            .Should().Be("(((Mig && true) || ((AuthMode == \"oidc\")) || ((Dash && true))))");
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

        var missing = Manifest.Features.Where(f => f.HasParameter)
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

    private static JsonObject Symbols(FeatureManifest manifest)
        => TemplateRepository.Symbols(SparkTemplateGenerator.Generate(manifest, TemplateRepository.HandWritten()).TemplateJson);

    private static Feature Feature(string id, string[]? requires = null, string? store = null, string? symbol = null, string? selectedWhen = null) => new()
    {
        Id = id,
        Flag = id.ToLowerInvariant(),
        DisplayName = id,
        Description = id,
        Project = $"x/{id}/{id}.csproj",
        Requires = [.. requires ?? []],
        Store = store,
        Symbol = symbol,
        SelectedWhen = selectedWhen,
    };
}
