using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// #254 — SPARK003. The model synchronizer already rejects a breadcrumb template that names an
/// ignored property, but only when someone runs <c>--spark-synchronize-model</c>. This analyzer
/// moves the failure to build time, where the two contradicting attributes are visible.
/// </summary>
public class IgnoredBreadcrumbFieldAnalyzerTests
{
    private const string AnalyzerName = "IgnoredBreadcrumbFieldAnalyzer";

    private static IEnumerable<Type> DefaultRefs =>
    [
        typeof(BreadcrumbAttribute),
        typeof(IgnorePropertyAttribute),
    ];

    [Fact]
    public async Task Breadcrumb_naming_an_ignored_property_raises_SPARK003()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [Breadcrumb("{LicensePlate} - {RegistrySyncEtag}")]
            public class Car
            {
                public string? Id { get; set; }
                public string LicensePlate { get; set; } = "";

                [IgnoreProperty]
                public string? RegistrySyncEtag { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK003");
        diagnostics[0].GetMessage().Should().Contain("RegistrySyncEtag");
    }

    [Fact]
    public async Task Breadcrumb_naming_only_modelled_properties_is_clean()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [Breadcrumb("{LicensePlate}")]
            public class Car
            {
                public string? Id { get; set; }
                public string LicensePlate { get; set; } = "";

                [IgnoreProperty]
                public string? RegistrySyncEtag { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().BeEmpty("an ignored property that the template does not name is fine");
    }

    [Fact]
    public async Task Breadcrumb_naming_an_ignored_property_on_a_base_type_raises_SPARK003()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class AuditedEntity
            {
                [IgnoreProperty]
                public string? SyncEtag { get; set; }
            }

            [Breadcrumb("{SyncEtag}")]
            public class Car : AuditedEntity
            {
                public string? Id { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().ContainSingle("the template may name an inherited property");
        diagnostics[0].Id.Should().Be("SPARK003");
    }

    [Fact]
    public async Task Type_without_a_Breadcrumb_attribute_is_clean()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class Car
            {
                public string? Id { get; set; }

                [IgnoreProperty]
                public string? RegistrySyncEtag { get; set; }
            }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], DefaultRefs);

        diagnostics.Should().BeEmpty();
    }
}
