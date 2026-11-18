using MintPlayer.Spark.Cli;

namespace MintPlayer.Spark.Cli.Tests;

/// <summary><see cref="SparkProject.Locate"/> finds the app and its entity library from wherever the command runs.</summary>
public sealed class SparkProjectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-cli-tests", Guid.NewGuid().ToString("N"));

    public SparkProjectTests()
    {
        // The layout spark-app generates: a solution with the app and its entity library.
        Write("Shop.slnx", "<Solution />");
        Write("Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><ProjectReference Include="..\Shop.Library\Shop.Library.csproj" /></ItemGroup></Project>""");
        Write("Shop/App_Data/security.json", "{}");
        Write("Shop/ShopContext.cs", "public partial class ShopContext : SparkContext { }");
        Write("Shop.Library/Shop.Library.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Directory.CreateDirectory(Path.Combine(root, "Shop.Library", "Entities"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Shop")]
    [InlineData("Shop/App_Data")]
    [InlineData("Shop.Library")]
    [InlineData("Shop.Library/Entities")]
    public void Locate_finds_the_app_and_library_from(string start)
    {
        var project = SparkProject.Locate(Path.Combine(root, start));

        project.Should().NotBeNull();
        project!.AppDirectory.Should().Be(Path.Combine(root, "Shop"));
        project.LibraryDirectory.Should().Be(Path.Combine(root, "Shop.Library"));
        project.ContextFile.Should().Be(Path.Combine(root, "Shop", "ShopContext.cs"));
    }

    [Fact]
    public void Locate_uses_the_app_as_library_when_it_references_none()
    {
        Write("Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web" />""");

        var project = SparkProject.Locate(Path.Combine(root, "Shop"));

        project!.LibraryDirectory.Should().Be(Path.Combine(root, "Shop"));
    }

    [Fact]
    public void Locate_returns_null_outside_a_Spark_app()
    {
        var elsewhere = Path.Combine(root, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            SparkProject.Locate(elsewhere).Should().BeNull();
        }
        finally
        {
            Directory.Delete(elsewhere);
        }
    }
}
