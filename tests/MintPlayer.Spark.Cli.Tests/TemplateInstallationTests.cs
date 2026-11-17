using MintPlayer.Spark.Cli;

namespace MintPlayer.Spark.Cli.Tests;

public class TemplateInstallationTests
{
    [Fact]
    public void ParseInstalledVersion_reads_the_version_under_the_package()
    {
        const string output = """
            Currently installed items:
               Microsoft.DotNet.Web.ProjectTemplates.11.0
                  Version: 11.0.0
                  Details:
                     Author: Microsoft
               MintPlayer.Spark.Templates
                  Version: 11.2.0-preview.3
                  Details:
                     Author: MintPlayer
                  Templates:
                     Spark app (spark-app) C#
                  Uninstall Command:
                     dotnet new uninstall MintPlayer.Spark.Templates
            """;

        TemplateInstallation.ParseInstalledVersion(output).Should().Be("11.2.0-preview.3");
    }

    [Fact]
    public void ParseInstalledVersion_returns_null_when_it_is_not_installed()
    {
        const string output = """
            Currently installed items:
               Microsoft.DotNet.Web.ProjectTemplates.11.0
                  Version: 11.0.0
            """;

        TemplateInstallation.ParseInstalledVersion(output).Should().BeNull();
    }
}
