using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Sparrow.Json.Parsing;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// #460, D5: Spark owns Data Protection. The load-bearing case is spike SP-B's compatibility half —
/// coverage.mintplayer.com's existing key ring, written by the repository CodeCoverage used to carry,
/// must still decrypt what it protected, or every production user is signed out once on deploy.
/// </summary>
public class SparkDataProtectionTests : SparkTestDriver
{
    private const string Purpose = "Spark.Tests.DataProtection";

    /// <summary>The metadata production documents carry, verbatim.</summary>
    private static readonly Dictionary<string, string> ProductionMetadata = new()
    {
        ["@collection"] = "KeyDocuments",
        ["@Raven-Clr-Type"] = "CodeCoverage.Services.RavenDataProtectionKeyRepository+KeyDocument, CodeCoverage",
    };

    private IDataProtectionProvider SparkProvider(Dictionary<string, string?> configuration, string environment = "Production")
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment, ContentRootPath = Path.GetTempPath() });
        services.AddSingleton(Store);
        services.AddLogging();
        services.AddSparkDataProtection();
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    /// <summary>
    /// Protects a payload through CodeCoverage's old wiring — <c>SetApplicationName("CodeCoverage")</c>
    /// over a repository writing <c>DataProtectionKeys/{name}</c> documents with an <c>Xml</c> string
    /// and the production type metadata — and returns the ciphertext.
    /// </summary>
    private string ProtectTheWayCodeCoverageDid(string payload)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("CodeCoverage");
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new LegacyCodeCoverageRepository(Store));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(payload);
    }

    [Fact]
    public void A_key_ring_written_by_CodeCoverage_decrypts_under_Spark()
    {
        var ciphertext = ProtectTheWayCodeCoverageDid("signed-in");

        var spark = SparkProvider(new()
        {
            ["Spark:DataProtection:ApplicationName"] = "CodeCoverage",
            ["Spark:DataProtection:Storage"] = "RavenDb",
        });

        spark.CreateProtector(Purpose).Unprotect(ciphertext).Should().Be("signed-in");
    }

    /// <summary>
    /// The negative that keeps the positive honest: the same key ring under another application
    /// name must NOT decrypt, or the test above would pass whatever the discriminator was.
    /// </summary>
    [Fact]
    public void The_application_name_is_part_of_the_contract()
    {
        var ciphertext = ProtectTheWayCodeCoverageDid("signed-in");

        var spark = SparkProvider(new()
        {
            ["Spark:DataProtection:ApplicationName"] = "SomethingElse",
            ["Spark:DataProtection:Storage"] = "RavenDb",
        });

        var act = () => spark.CreateProtector(Purpose).Unprotect(ciphertext);

        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void New_keys_land_under_the_same_prefix_and_collection()
    {
        var spark = SparkProvider(new() { ["Spark:DataProtection:Storage"] = "RavenDb" });

        spark.CreateProtector(Purpose).Protect("anything");

        using var session = Store.OpenSession();
        var stored = session.Advanced.LoadStartingWith<RavenDataProtectionKeyRepository.KeyDocument>(
            RavenDataProtectionKeyRepository.IdPrefix);

        stored.Should().ContainSingle();
        stored[0].Id.Should().StartWith("DataProtectionKeys/");
        session.Advanced.GetMetadataFor(stored[0])["@collection"].Should().Be("KeyDocuments");
    }

    [Fact]
    public void An_unpersisted_key_ring_refuses_to_start_outside_Development()
    {
        var act = () => SparkDataProtection.Validate(
            new SparkDataProtectionOptions(), new HostingEnvironment { EnvironmentName = "Production" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*signs every user out on every redeploy*");
    }

    [Fact]
    public void Development_needs_no_configuration()
    {
        var act = () => SparkDataProtection.Validate(
            new SparkDataProtectionOptions(), new HostingEnvironment { EnvironmentName = Environments.Development });

        act.Should().NotThrow();
    }

    [Fact]
    public void Naming_two_places_for_the_key_ring_is_refused()
    {
        var act = () => SparkDataProtection.Validate(
            new SparkDataProtectionOptions { KeysPath = "/keys", Storage = SparkDataProtectionStorage.RavenDb },
            new HostingEnvironment { EnvironmentName = Environments.Development });

        act.Should().Throw<InvalidOperationException>().WithMessage("*both KeysPath*");
    }

    [Fact]
    public void A_keys_path_persists_the_ring_on_disk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "spark-dp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ciphertext = SparkProvider(new() { ["Spark:DataProtection:KeysPath"] = folder })
                .CreateProtector(Purpose).Protect("persisted");

            Directory.EnumerateFiles(folder, "key-*.xml").Should().ContainSingle();

            // A second provider over the same folder — a restarted process — reads it back.
            SparkProvider(new() { ["Spark:DataProtection:KeysPath"] = folder })
                .CreateProtector(Purpose).Unprotect(ciphertext).Should().Be("persisted");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// CodeCoverage's repository as it shipped, except that the write goes through a raw put
    /// command so the stored metadata is byte-for-byte what production holds (a test type would stamp
    /// its own CLR type name). Reads stay in memory: the old read path is not what is under test.
    /// </summary>
    private sealed class LegacyCodeCoverageRepository(IDocumentStore store) : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements() => _elements.ToList();

        public void StoreElement(XElement element, string friendlyName)
        {
            _elements.Add(element);

            var metadata = new DynamicJsonValue();
            foreach (var (key, value) in ProductionMetadata)
                metadata[key] = value;

            using var session = store.OpenSession();
            session.Advanced.Defer(new PutCommandData(
                "DataProtectionKeys/" + friendlyName,
                changeVector: null,
                new DynamicJsonValue
                {
                    ["Xml"] = element.ToString(SaveOptions.DisableFormatting),
                    ["@metadata"] = metadata,
                }));
            session.SaveChanges();
        }
    }
}
