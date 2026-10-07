using System.Reflection;
using System.Reflection.Emit;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <see cref="SparkAppData"/> resolves the <c>SparkAppDataDir</c> the build stamped into the application
/// assembly (composition PRD D12): relative against the content root, absolute as-is, and
/// <c>App_Data</c> when the entry assembly carries nothing (a test runner, a tool).
/// </summary>
public class SparkAppDataTests
{
    private static readonly string ContentRoot = Path.Combine(Path.GetTempPath(), "spark-appdata-root");

    [Fact]
    public void Missing_metadata_falls_back_to_App_Data()
    {
        SparkAppData.Configured(null).Should().Be("App_Data");
        SparkAppData.Configured(typeof(SparkAppDataTests).Assembly).Should().Be("App_Data");
    }

    [Fact]
    public void A_host_without_metadata_resolves_App_Data_under_the_content_root()
    {
        // The test runner is the entry assembly here, and it carries no SparkAppDataDir.
        SparkAppData.RelativeDirectory.Should().Be("App_Data");
        SparkAppData.Directory(ContentRoot).Should().Be(Path.Combine(ContentRoot, "App_Data"));
        SparkAppData.Path(ContentRoot, "Model").Should().Be(Path.Combine(ContentRoot, "App_Data", "Model"));
        SparkAppData.Relative("security.json").Should().Be("App_Data/security.json");
    }

    [Theory]
    [InlineData(@"Config\Spark", "Config/Spark")]
    [InlineData("Config/Spark/", "Config/Spark")]
    [InlineData("  ", "App_Data")]
    public void The_stamped_value_is_read_and_normalised(string stamped, string expected)
    {
        SparkAppData.Configured(AssemblyStampedWith(stamped)).Should().Be(expected);
    }

    [Fact]
    public void A_relative_directory_is_combined_with_the_content_root()
    {
        var configured = SparkAppData.Configured(AssemblyStampedWith(@"Config\Spark"));

        SparkAppData.Resolve(ContentRoot, configured)
            .Should().Be(Path.Combine(ContentRoot, "Config", "Spark"));
    }

    [Fact]
    public void An_absolute_directory_is_used_as_is()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "spark-shared-config");
        var configured = SparkAppData.Configured(AssemblyStampedWith(absolute));

        SparkAppData.Resolve(ContentRoot, configured).Should().Be(absolute);
    }

    /// <summary>A dynamic assembly carrying what spark.targets writes: <c>[assembly: AssemblyMetadata("SparkAppDataDir", value)]</c>.</summary>
    private static Assembly AssemblyStampedWith(string value)
    {
        var ctor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        return AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("SparkAppDataProbe" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run,
            [new CustomAttributeBuilder(ctor, [SparkAppData.MetadataKey, value])]);
    }
}
