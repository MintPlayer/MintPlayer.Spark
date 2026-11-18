using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>
/// Why provisioning the embedded server failed, named in the error (templates plan M6, 2026-11-18). A generated app's
/// test once failed with RavenDB's bare "Value cannot be null. (Parameter 'ServerDirectory')": the locator had four
/// silent ways to return null and the error could not say which one fired.
/// </summary>
public class RavenServerProvisioningTests : IDisposable
{
    private const string Version = "9.9.9-test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spark-provisioning-tests", Guid.NewGuid().ToString("N"));

    private string PackageRoot => Path.Combine(_root, "packages");
    private string TempRoot => Path.Combine(_root, "temp");
    private string Source => Path.Combine(PackageRoot, "ravendb.embedded", Version, "contentFiles", "any", "any", "RavenDBServer");

    public RavenServerProvisioningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void No_embedded_package_in_the_deps_files_names_that()
    {
        var result = RavenServerLocator.Provision(null, PackageRoot, TempRoot);

        result.Directory.Should().BeNull();
        result.Reason.Should().Contain("RavenDB.Embedded");
        result.Reason.Should().Contain(".deps.json");
    }

    [Fact]
    public void A_package_without_the_server_names_where_it_looked()
    {
        var result = RavenServerLocator.Provision(Version, PackageRoot, TempRoot);

        result.Directory.Should().BeNull();
        result.Reason.Should().Contain(Path.Combine(Source, "Raven.Server.dll"));
    }

    [Fact]
    public void A_failed_copy_names_the_exception()
    {
        CreateFakeServer();
        File.WriteAllText(TempRoot, "a file where the temp directory should be");

        var result = RavenServerLocator.Provision(Version, PackageRoot, TempRoot);

        result.Directory.Should().BeNull();
        result.Reason.Should().Contain("IOException");
    }

    [Fact]
    public void A_completed_copy_returns_the_directory()
    {
        CreateFakeServer();

        var result = RavenServerLocator.Provision(Version, PackageRoot, TempRoot);

        result.Reason.Should().BeNull();
        File.Exists(Path.Combine(result.Directory!, "Raven.Server.dll")).Should().BeTrue();
    }

    [Fact]
    public void Requiring_the_directory_of_a_failed_provisioning_throws_with_the_reason()
    {
        var result = RavenServerLocator.Provision(Version, PackageRoot, TempRoot);

        Action act = () => result.RequireDirectory();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(Path.Combine(Source, "Raven.Server.dll"));
    }

    private void CreateFakeServer()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "Raven.Server.dll"), "not really a server");
    }
}
