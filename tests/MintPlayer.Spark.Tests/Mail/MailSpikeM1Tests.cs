using System.Globalization;
using Xunit.Abstractions;
using System.Reflection;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #460 spike S-M1: where MSBuild puts an embedded <c>.mjml</c> template for each naming layout —
/// in the main assembly, or moved into a satellite assembly by <c>AssignCulture</c> — and whether a
/// culture-suffixed <c>Content</c> file is copied as-is. The fixtures are under
/// <c>Mail/SpikeM1Templates</c>; the item groups are in the test csproj.
/// </summary>
public class MailSpikeM1Tests(ITestOutputHelper output)
{
    private static readonly Assembly Self = typeof(MailSpikeM1Tests).Assembly;

    [Fact]
    public void S_M1_where_embedded_mjml_resources_land()
    {
        var main = Self.GetManifestResourceNames().Where(n => n.Contains("SpikeM1Templates") || n.StartsWith("SparkMailSpike", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        foreach (var name in main)
            output.WriteLine($"main: {name}");

        var satellites = new Dictionary<string, List<string>>();
        foreach (var culture in new[] { "nl", "nl-BE" })
        {
            var dll = Path.Combine(AppContext.BaseDirectory, culture, "MintPlayer.Spark.Tests.resources.dll");
            output.WriteLine($"satellite {culture}: exists={File.Exists(dll)}");
            try
            {
                var satellite = Self.GetSatelliteAssembly(new CultureInfo(culture));
                satellites[culture] = [.. satellite.GetManifestResourceNames().Where(n => n.Contains("SpikeM1Templates")).Order(StringComparer.Ordinal)];
                foreach (var name in satellites[culture])
                    output.WriteLine($"satellite {culture}: {name}");
            }
            catch (FileNotFoundException)
            {
                satellites[culture] = [];
            }
        }

        var contentDir = Path.Combine(AppContext.BaseDirectory, "Mail", "SpikeM1Templates", "content");
        var content = Directory.Exists(contentDir) ? Directory.GetFiles(contentDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList() : [];
        foreach (var file in content)
            output.WriteLine($"content: {file}");

        // {lang}/{name}.mjml: stays in the main assembly; the folder's '-' becomes '_' in the name.
        main.Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.folder.nl.Welcome.mjml");
        main.Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.folder.nl_BE.Welcome.mjml");
        main.Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.folder.neutral.Welcome.mjml");

        // {name}.{lang}.mjml: AssignCulture moves the culture-suffixed files into satellites.
        main.Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.suffix.Welcome.mjml");
        main.Should().NotContain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.suffix.Welcome.nl.mjml");
        satellites["nl"].Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.suffix.Welcome.mjml");
        satellites["nl-BE"].Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.suffix.Welcome.mjml");

        // WithCulture="false" keeps them in the main assembly under their full name.
        main.Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.suffixnoculture.Welcome.nl.mjml");
        main.Should().Contain("MintPlayer.Spark.Tests.Mail.SpikeM1Templates.suffixnoculture.Welcome.nl-BE.mjml");

        // LogicalName + WithCulture="false": the literal path, folder separator as MSBuild wrote it.
        main.Should().Contain(n => n.StartsWith("SparkMailSpike/Auth", StringComparison.Ordinal) && n.EndsWith("Welcome.nl-BE.mjml", StringComparison.Ordinal));

        // Content files are copied under their own names; AssignCulture only touches EmbeddedResource.
        content.Should().Equal(["Welcome.nl-BE.mjml", "Welcome.nl.mjml"]);
    }
}
