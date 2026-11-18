using MintPlayer.Spark.Cli;
using MintPlayer.Spark.Templates.Build;

namespace MintPlayer.Spark.Cli.Tests;

/// <summary>The checks <c>spark new</c> makes before it runs the template, against the embedded features.json.</summary>
public class NewRequestTests
{
    private static readonly FeatureManifest Manifest = Features.Manifest;

    private static NewRequest Request(string store = "RavenDB", string auth = "none", string? authority = null, string[]? features = null, bool noClient = false)
        => new("Shop", "Shop", store, auth, authority, features, noClient);

    [Fact]
    public void The_embedded_manifest_holds_the_features()
    {
        Manifest.Features.Should().Contain(f => f.Id == "Moderation");
    }

    [Fact]
    public void Defaults_are_valid_and_select_the_default_features()
    {
        var request = Request();

        request.Validate(Manifest).Should().BeEmpty();
        request.SelectedFlags(Manifest).Should().Contain("migrations");
    }

    [Fact]
    public void An_unknown_feature_is_refused()
    {
        Request(features: ["migrations", "teleport"]).Validate(Manifest).Should().ContainSingle()
            .Which.Should().Contain("teleport");
    }

    [Fact]
    public void A_RavenDB_feature_is_refused_on_InMemory()
    {
        Request(store: "InMemory", features: ["history"]).Validate(Manifest).Should().ContainSingle()
            .Which.Should().Contain("history");
    }

    [Fact]
    public void Sign_in_is_refused_on_InMemory()
    {
        Request(store: "InMemory", auth: "authorization", features: []).Validate(Manifest).Should().ContainSingle()
            .Which.Should().Contain("InMemory");
    }

    [Fact]
    public void Oidc_needs_an_authority()
    {
        Request(auth: "oidc").Validate(Manifest).Should().ContainSingle().Which.Should().Contain("--oidc-authority");
        Request(auth: "oidc", authority: "https://id.example.com").Validate(Manifest).Should().BeEmpty();
    }

    [Fact]
    public void Prerequisites_are_reported_as_added()
    {
        var request = Request(features: ["moderation"]);

        request.Validate(Manifest).Should().BeEmpty();
        var added = request.AddedPrerequisites(Manifest);
        added.Should().Contain("Authorization");
        added.Should().Contain("Cron");
        added.Should().Contain("Messaging");
    }

    [Fact]
    public void TemplateArguments_switch_every_feature_on_or_off_explicitly()
    {
        var arguments = Request(features: ["history"]).TemplateArguments(Manifest);

        arguments.Should().StartWith(["new", "spark-app", "--name", "Shop", "--output", "Shop", "--store", "RavenDB", "--auth", "none"]);
        Pair(arguments, "--history").Should().Be("true");
        Pair(arguments, "--migrations").Should().Be("false");
    }

    [Fact]
    public void TemplateArguments_leave_out_features_the_store_disables()
    {
        var arguments = Request(store: "InMemory", features: []).TemplateArguments(Manifest);

        arguments.Should().NotContain("--history");
        arguments.Should().Contain("--logging");
    }

    private static string? Pair(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        return index < 0 ? null : arguments[index + 1];
    }
}
