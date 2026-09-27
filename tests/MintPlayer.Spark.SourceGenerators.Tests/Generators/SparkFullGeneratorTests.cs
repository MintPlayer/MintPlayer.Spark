using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Tests for the AllFeatures composition generator that emits the top-level
/// <c>AddSparkFull</c>/<c>UseSparkFull</c>/<c>MapSparkFull</c> extension methods.
/// Lives in a separate generator assembly; loaded via <c>generatorAssemblyName</c>.
/// </summary>
public class SparkFullGeneratorTests
{
    private const string GeneratorName = "SparkFullGenerator";
    private const string GeneratorAssembly = "MintPlayer.Spark.AllFeatures.SourceGenerators";

    [Fact]
    public void Emits_nothing_when_Spark_is_not_referenced()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        // Without a Spark reference (SparkContext is the gate), the generator doesn't
        // emit the AddSparkFull extension — nothing for it to wire up.
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void SparkContext_subclass_is_woven_into_the_generated_registration()
    {
        var source = """
            using MintPlayer.Spark;

            namespace TestApp;

            public class AppContext : SparkContext { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        combined.Should().Contain("TestApp.AppContext");
    }

    [Fact]
    public void SparkUser_subclass_is_routed_through_AddAuthentication()
    {
        var source = """
            using MintPlayer.Spark;
            using MintPlayer.Spark.Authorization.Identity;

            namespace TestApp;

            public class AppContext : SparkContext { }
            public class AppUser : SparkUser { public string DisplayName { get; set; } = ""; }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext), typeof(SparkUser)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        combined.Should().Contain("TestApp.AppUser");
        combined.Should().Contain("AddAuthentication");
    }

    [Fact]
    public void Cron_job_is_wired_into_AddSparkFull_via_fully_qualified_call()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark;
            using MintPlayer.Spark.Cron;

            namespace TestApp;

            public class AppContext : SparkContext { }

            public class NightlyCleanup : ISparkCronJob
            {
                public static string CronSchedule => "0 0 * * *";
                public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext), typeof(ISparkCronJob)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        // Emitted as a fully-qualified static call (not extension-method syntax) to avoid collisions.
        combined.Should().Contain("global::TestApp.SparkCronJobsBuilderExtensions.AddCronJobs(spark)");
    }

    [Fact]
    public void Migration_is_wired_into_AddSparkFull_via_fully_qualified_call()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark;
            using MintPlayer.Spark.Migrations;

            namespace TestApp;

            public class AppContext : SparkContext { }

            public class AddInitialData : ISparkMigration
            {
                public static long Version => 202606081200;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext), typeof(ISparkMigration)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        // Emitted as a fully-qualified static call (not extension-method syntax) to avoid collisions.
        combined.Should().Contain("global::TestApp.SparkMigrationsBuilderExtensions.AddMigrations(spark)");
    }

    [Fact]
    public void Actions_custom_actions_and_recipients_are_each_wired_in()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark;
            using MintPlayer.Spark.Actions;
            using MintPlayer.Spark.Abstractions.Actions;
            using MintPlayer.Spark.Messaging.Abstractions;

            namespace TestApp;

            public class AppContext : SparkContext { }

            public class Car { public string? Id { get; set; } }

            // Found through the base-type walk, one level removed.
            public class VehicleActions<T> : DefaultPersistentObjectActions<T> where T : class { }
            public class CarActions : VehicleActions<Car> { }

            public class ExportAction : ICustomAction
            {
                public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }

            public record CarSold(string Plate);
            public class CarSoldRecipient : IRecipient<CarSold>
            {
                public Task HandleAsync(CarSold message, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }

            // Neither is anything the bundle wires: an abstract base and an unrelated derived class.
            public abstract class BaseAction : ICustomAction
            {
                public abstract Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default);
            }
            public class Unrelated : System.Exception { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext), typeof(MintPlayer.Spark.Actions.DefaultPersistentObjectActions<>), typeof(ICustomAction), typeof(IRecipient<>)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        combined.Should().Contain("global::TestApp.SparkActionsBuilderExtensions.AddActions(spark)");
        combined.Should().Contain("global::TestApp.SparkCustomActionsBuilderExtensions.AddCustomActions(spark)");
        combined.Should().Contain("global::TestApp.SparkRecipientsBuilderExtensions.AddRecipients(spark)");
        combined.Should().NotContain("AddCronJobs");
        combined.Should().NotContain("AddMigrations");
    }

    /// <summary>
    /// Messaging and replication are detected by their builder-extension types being in the
    /// compilation. Declared in the source here so the test needs no reference to either package —
    /// the generator looks the types up by metadata name and cannot tell the difference.
    /// </summary>
    [Fact]
    public void Referenced_messaging_replication_and_authorization_are_wired_in()
    {
        var source = """
            using MintPlayer.Spark;

            namespace TestApp
            {
                public class AppContext : SparkContext { }
            }

            namespace MintPlayer.Spark.Messaging
            {
                public static class SparkBuilderMessagingExtensions { }
            }

            namespace MintPlayer.Spark.Replication
            {
                public static class SparkBuilderReplicationExtensions { }
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext), typeof(SparkUser)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        combined.Should().Contain("SparkBuilderMessagingExtensions.AddMessaging(spark, options.Messaging)");
        combined.Should().Contain("if (options.Replication != null)");
        combined.Should().Contain("SparkBuilderReplicationExtensions.AddReplication(spark, options.Replication)");
        // No SparkUser subclass, but Authorization is referenced: the base type is used.
        combined.Should().Contain("AddAuthentication<global::MintPlayer.Spark.Authorization.Identity.SparkUser>");
    }

    [Fact]
    public void Emits_nothing_without_a_SparkContext_subclass()
    {
        var source = """
            namespace TestApp;
            public class NotAContext : System.Exception { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        string.Join("\n", result.GeneratedSources.Select(s => s.Source)).Should().NotContain("AddSparkFull");
    }

    [Fact]
    public void Without_a_root_namespace_the_code_lands_in_GeneratedCode()
    {
        var source = """
            using MintPlayer.Spark;
            namespace TestApp;
            public class AppContext : SparkContext { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext)],
            rootNamespace: null,
            generatorAssemblyName: GeneratorAssembly);

        string.Join("\n", result.GeneratedSources.Select(s => s.Source)).Should().Contain("namespace GeneratedCode");
    }
}
