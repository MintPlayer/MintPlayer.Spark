using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using MintPlayer.Spark.SubscriptionWorker;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// X20 item 3: one discovery generator emits <c>AddSparkDiscovered()</c>, replacing the seven
/// per-kind registration generators (Actions, custom actions, recipients, cron jobs, migrations,
/// interceptors, subscription workers) and adding row policies and <c>[SparkDiscoverable]</c> contracts.
/// The per-kind assertions are those of the former generators' tests, carried over.
/// </summary>
public class SparkDiscoveryGeneratorTests
{
    // Loaded dynamically — can't use `nameof` because the generator DLL isn't referenced at compile time.
    private const string GeneratorName = "SparkDiscoveryGenerator";

    /// <summary>Runs the generator with <c>ISparkBuilder</c> always resolvable: the generated method's signature names it.</summary>
    private static GeneratorRunResult Run(
        IEnumerable<string> sources,
        IEnumerable<Type> referenceTypes,
        string? rootNamespace = "TestApp",
        Microsoft.CodeAnalysis.OutputKind outputKind = Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
        IEnumerable<Microsoft.CodeAnalysis.MetadataReference>? additionalReferences = null)
        => GeneratorHarness.Run(
            GeneratorName,
            sources,
            referenceTypes: referenceTypes.Append(typeof(ISparkBuilder)),
            rootNamespace: rootNamespace,
            outputKind: outputKind,
            additionalReferences: additionalReferences);

    private static string Single(GeneratorRunResult result)
    {
        result.GeneratedSources.Should().ContainSingle();
        return result.GeneratedSources[0].Source;
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ── The entry point ───────────────────────────────────────────────────────────

    [Fact]
    public void No_source_without_ISparkBuilder()
    {
        var source = """
            namespace TestApp;

            public class Foo
            {
                public int Bar { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(GeneratorName, [source], referenceTypes: Array.Empty<Type>(), rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void No_source_when_nothing_is_discovered()
    {
        var result = Run(["namespace TestApp; public class Foo { }"],
            [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>), typeof(ICustomAction), typeof(IRecipient<>), typeof(ISparkCronJob), typeof(ISparkMigration)]);

        result.GeneratedSources.Should().BeEmpty();
    }

    /// <summary>AddSparkFull calls AddSparkDiscovered unconditionally, so an AllFeatures app gets it even with nothing to register.</summary>
    [Fact]
    public void An_AllFeatures_app_gets_the_entry_point_even_when_nothing_is_discovered()
    {
        var source = """
            namespace MintPlayer.Spark.AllFeatures
            {
                public class SparkFullOptions { }
            }
            namespace TestApp
            {
                public class Foo { }
            }
            """;

        var generated = Single(Run([source], []));

        generated.Should().Contain("internal static class SparkDiscoveryBuilderExtensions");
        generated.Should().Contain("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddSparkDiscovered(this global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder builder)");
        generated.Should().Contain("return builder;");
    }

    [Fact]
    public void Without_a_root_namespace_the_code_lands_in_GeneratedCode()
    {
        var source = """
            using MintPlayer.Spark.Cron;
            namespace TestApp;
            public class NightlyCleanup : ISparkCronJob
            {
                public static string CronSchedule => "0 0 * * *";
                public System.Threading.Tasks.Task RunAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
            }
            """;

        Single(Run([source], [typeof(ISparkCronJob)], rootNamespace: null)).Should().Contain("namespace GeneratedCode");
    }

    // ── Actions classes ───────────────────────────────────────────────────────────

    [Fact]
    public void Single_actions_class_is_registered()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using MintPlayer.Spark.Actions;

            namespace TestApp.Actions;

            public partial class WidgetActions : DefaultPersistentObjectActions<Widget>
            {
                public WidgetActions() : base(null!, null!, null!, null!) { }
            }

            public class Widget : PersistentObject { }
            """;

        var result = Run([source], [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)]);

        result.GeneratedSources.Should().ContainSingle();
        var (hintName, generated) = result.GeneratedSources[0];

        hintName.Should().Be("SparkDiscoveryRegistrations.g.cs");
        generated.Should().Contain("internal static class SparkDiscoveryBuilderExtensions");
        generated.Should().Contain("AddSparkActions<global::TestApp.Actions.WidgetActions, global::TestApp.Actions.Widget>(builder.Services);");
        generated.Should().Contain("return builder;");
    }

    [Fact]
    public void Multiple_actions_classes_each_registered_in_deterministic_order()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using MintPlayer.Spark.Actions;

            namespace TestApp.Actions;

            public partial class CarActions : DefaultPersistentObjectActions<Car>
            {
                public CarActions() : base(null!, null!, null!, null!) { }
            }

            public partial class PersonActions : DefaultPersistentObjectActions<Person>
            {
                public PersonActions() : base(null!, null!, null!, null!) { }
            }

            public class Car : PersistentObject { }
            public class Person : PersistentObject { }
            """;

        var generated = Single(Run([source], [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)]));

        var car = generated.IndexOf("AddSparkActions<global::TestApp.Actions.CarActions, global::TestApp.Actions.Car>", StringComparison.Ordinal);
        var person = generated.IndexOf("AddSparkActions<global::TestApp.Actions.PersonActions, global::TestApp.Actions.Person>", StringComparison.Ordinal);
        car.Should().BeGreaterThan(-1);
        person.Should().BeGreaterThan(car);
    }

    [Fact]
    public void Actions_class_found_through_an_intermediate_base()
    {
        var source = """
            using MintPlayer.Spark.Actions;

            namespace TestApp;

            public class Car { public string? Id { get; set; } }
            public abstract class VehicleActions<T> : DefaultPersistentObjectActions<T> where T : class { }
            public class CarActions : VehicleActions<Car> { }
            """;

        Single(Run([source], [typeof(DefaultPersistentObjectActions<>)]))
            .Should().Contain("AddSparkActions<global::TestApp.CarActions, global::TestApp.Car>(builder.Services);");
    }

    [Fact]
    public void Abstract_actions_class_is_skipped()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using MintPlayer.Spark.Actions;

            namespace TestApp.Actions;

            public abstract partial class BaseActions<T> : DefaultPersistentObjectActions<T>
                where T : PersistentObject, new()
            {
                protected BaseActions() : base(null!, null!, null!, null!) { }
            }

            public class Widget : PersistentObject { }
            """;

        Run([source], [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)])
            .GeneratedSources.Should().BeEmpty("abstract classes shouldn't be registered in DI");
    }

    // ── Custom actions ────────────────────────────────────────────────────────────

    [Fact]
    public void ICustomAction_implementation_is_registered_as_scoped_service()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;

            namespace TestApp.Actions;

            public class ExportAction : ICustomAction
            {
                public Task ExecuteAsync(CustomActionArgs args, CancellationToken ct = default)
                    => Task.CompletedTask;
            }
            """;

        var generated = Single(Run([source], [typeof(ICustomAction)]));

        generated.Should().Contain("global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<global::TestApp.Actions.ExportAction>(builder.Services);");
    }

    [Fact]
    public void Abstract_ICustomAction_is_skipped()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;

            namespace TestApp.Actions;

            public abstract class BaseAction : ICustomAction
            {
                public abstract Task ExecuteAsync(CustomActionArgs args, CancellationToken ct = default);
            }
            """;

        Run([source], [typeof(ICustomAction)]).GeneratedSources.Should().BeEmpty();
    }

    /// <summary>A partial class is seen once per declaration that carries a base list; it is registered once.</summary>
    [Fact]
    public void A_partial_class_declared_twice_is_registered_once()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;

            namespace TestApp.Actions;

            public partial class ExportAction : ICustomAction
            {
                public Task ExecuteAsync(CustomActionArgs args, CancellationToken ct = default)
                    => Task.CompletedTask;
            }

            public partial class ExportAction : ICustomAction { }
            """;

        Count(Single(Run([source], [typeof(ICustomAction)])), "AddScoped<global::TestApp.Actions.ExportAction>").Should().Be(1);
    }

    // ── Recipients ────────────────────────────────────────────────────────────────

    [Fact]
    public void IRecipient_implementation_is_registered_with_the_message_type()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Messaging.Abstractions;

            namespace TestApp.Messaging;

            public record OrderPlaced(string Id, decimal Amount);

            public class OrderHandler : IRecipient<OrderPlaced>
            {
                public Task HandleAsync(OrderPlaced m, CancellationToken ct = default)
                    => Task.CompletedTask;
            }
            """;

        var generated = Single(Run([source], [typeof(IRecipient<>)]));

        generated.Should().Contain("IRecipient<global::TestApp.Messaging.OrderPlaced>, global::TestApp.Messaging.OrderHandler>(builder.Services);");
        // The checkpoint-specific registration must NOT appear for a plain IRecipient handler.
        generated.Should().NotContain("ICheckpointRecipient");
    }

    [Fact]
    public void ICheckpointRecipient_implementation_registers_both_IRecipient_and_ICheckpointRecipient()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Messaging.Abstractions;

            namespace TestApp.Messaging;

            public record BigJob(string Id);

            public class BigJobHandler : ICheckpointRecipient<BigJob>
            {
                public Task HandleAsync(BigJob m, CancellationToken ct = default)
                    => Task.CompletedTask;
                public Task HandleAsync(BigJob m, string checkpoint, CancellationToken ct = default)
                    => Task.CompletedTask;
            }
            """;

        var generated = Single(Run([source], [typeof(IRecipient<>), typeof(ICheckpointRecipient<>)]));

        generated.Should().Contain("IRecipient<global::TestApp.Messaging.BigJob>, global::TestApp.Messaging.BigJobHandler");
        generated.Should().Contain("ICheckpointRecipient<global::TestApp.Messaging.BigJob>, global::TestApp.Messaging.BigJobHandler");
    }

    [Fact]
    public void A_lookalike_IRecipient_is_not_a_recipient()
    {
        // Same name, another namespace: nothing the generator keys on.
        var source = """
            namespace TestApp;
            public interface IRecipient<T> { }
            public class OrderHandler : IRecipient<string> { }
            """;

        Run([source], []).GeneratedSources.Should().BeEmpty();
    }

    // ── Cron jobs ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ISparkCronJob_implementer_is_registered()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Cron;

            namespace TestApp.Jobs;

            public class NightlyCleanup : ISparkCronJob
            {
                public static string CronSchedule => "0 0 * * *";
                public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var generated = Single(Run([source], [typeof(ISparkCronJob)]));

        generated.Should().Contain("global::MintPlayer.Spark.Cron.SparkCronExtensions.AddCron(builder, cron =>");
        generated.Should().Contain("cron.AddJob<global::TestApp.Jobs.NightlyCleanup>();");
    }

    // ── Migrations ────────────────────────────────────────────────────────────────

    [Fact]
    public void Multiple_migrations_are_all_registered()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace TestApp.Migrations;

            public class AddInitialData : ISparkMigration
            {
                public static long Version => 202606081200;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public class SeedLookups : ISparkMigration
            {
                public static long Version => 202606081300;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var generated = Single(Run([source], [typeof(ISparkMigration)]));

        generated.Should().Contain("global::MintPlayer.Spark.Migrations.SparkMigrationsExtensions.AddMigrations(builder, migrations =>");
        generated.Should().Contain("migrations.AddMigration<global::TestApp.Migrations.AddInitialData>()");
        generated.Should().Contain("migrations.AddMigration<global::TestApp.Migrations.SeedLookups>()");
    }

    [Fact]
    public void Abstract_migration_is_skipped()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace TestApp.Migrations;

            public abstract class MigrationBase : ISparkMigration
            {
                public static long Version => 0;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        Run([source], [typeof(ISparkMigration)]).GeneratedSources.Should().BeEmpty();
    }

    private const string AppWithoutMigrations = """
        namespace TestApp;
        public static class Program { public static void Main() { } }
        """;

    private static Microsoft.CodeAnalysis.MetadataReference Package(string accessibility) =>
        GeneratorHarness.CompileToMetadataReference(
            "Acme.Package",
            [$$"""
                using System.Threading;
                using System.Threading.Tasks;
                using MintPlayer.Spark.Migrations;

                namespace Acme.Package.Migrations;

                {{accessibility}} class M_202610041800_Fix : ISparkMigration
                {
                    public static long Version => 202610041800;
                    public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                }
                """],
            [typeof(ISparkMigration)]);

    [Fact]
    public void A_host_registers_the_public_migrations_of_a_referenced_package()
    {
        var result = Run([AppWithoutMigrations], [typeof(ISparkMigration)],
            outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [Package("public")]);

        // The app has no migration of its own, yet still gets the registration (QnA's shape).
        Single(result).Should().Contain("migrations.AddMigration<global::Acme.Package.Migrations.M_202610041800_Fix>()");
    }

    [Fact]
    public void A_host_registers_its_own_migrations_before_the_packages()
    {
        var own = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace TestApp.Migrations;

            public class M_202601010000_Own : ISparkMigration
            {
                public static long Version => 202601010000;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var generated = Single(Run([AppWithoutMigrations, own], [typeof(ISparkMigration)],
            outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [Package("public")]));

        var ownAt = generated.IndexOf("M_202601010000_Own", StringComparison.Ordinal);
        var packageAt = generated.IndexOf("M_202610041800_Fix", StringComparison.Ordinal);
        ownAt.Should().BeGreaterThan(-1);
        packageAt.Should().BeGreaterThan(ownAt);
    }

    [Fact]
    public void A_non_public_package_migration_cannot_be_registered()
    {
        var result = Run([AppWithoutMigrations], [typeof(ISparkMigration)],
            outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [Package("internal")]);

        // The generated code could not name it; SPARK037 warns in the package instead.
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void A_library_does_not_register_a_referenced_package_migration()
    {
        // Only the application registers: a library between the package and the app must not
        // register migrations that are not its own.
        var result = Run(["namespace TestLib; public class Foo { }"], [typeof(ISparkMigration)],
            rootNamespace: "TestLib",
            additionalReferences: [Package("public")]);

        result.GeneratedSources.Should().BeEmpty();
    }

    // ── Interceptors ──────────────────────────────────────────────────────────────

    [Fact]
    public void Interceptors_are_registered_and_Actions_classes_are_not()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions;
            using MintPlayer.Spark.Abstractions.Interceptors;
            using MintPlayer.Spark.Actions;

            namespace TestApp;

            public class Car { public string? Id { get; set; } }

            public class CarAudit : IAfterSave<Car>
            {
                public ValueTask OnAfterSaveAsync(Car entity, SaveContext context) => ValueTask.CompletedTask;
            }

            internal sealed class Stamping : IBeforeSave
            {
                public bool AppliesTo(Type entityType) => true;
                public ValueTask OnBeforeSaveAsync(SaveContext context) => ValueTask.CompletedTask;
            }

            // Run as its type's interceptor without registration: never registered as an interceptor.
            public class CarActions : DefaultPersistentObjectActions<Car>, IBeforeDelete<Car>
            {
                public CarActions(IEntityMapper mapper) : base(mapper) { }
                public ValueTask OnBeforeDeleteAsync(Car entity, DeleteContext context) => ValueTask.CompletedTask;
            }

            public abstract class AbstractInterceptor : IAfterLoad
            {
                public bool AppliesTo(Type entityType) => true;
                public ValueTask OnAfterLoadAsync(LoadContext context) => ValueTask.CompletedTask;
            }

            public class GenericInterceptor<T> : IBeforeSave
            {
                public bool AppliesTo(Type entityType) => true;
                public ValueTask OnBeforeSaveAsync(SaveContext context) => ValueTask.CompletedTask;
            }
            """;

        var generated = Single(Run([source], [typeof(ISparkInterceptor), typeof(DefaultPersistentObjectActions<>)]));

        generated.Should().Contain("SparkBuilderInterceptorExtensions.AddInterceptor<global::TestApp.CarAudit>(builder);");
        generated.Should().Contain("SparkBuilderInterceptorExtensions.AddInterceptor<global::TestApp.Stamping>(builder);");
        generated.Should().NotContain("AddInterceptor<global::TestApp.CarActions>");
        generated.Should().NotContain("AbstractInterceptor");
        generated.Should().NotContain("GenericInterceptor");
        // The Actions class itself is still registered as an Actions class.
        generated.Should().Contain("AddSparkActions<global::TestApp.CarActions, global::TestApp.Car>");
    }

    // ── Subscription workers ──────────────────────────────────────────────────────

    /// <summary>
    /// The registration names <c>SparkSubscriptionExtensions</c>, which lives in the runtime package. Declared in
    /// source here: the generator looks it up by metadata name and cannot tell the difference.
    /// </summary>
    private const string SubscriptionRuntime = """
        namespace MintPlayer.Spark.SubscriptionWorker
        {
            public static class SparkSubscriptionExtensions { }
        }
        """;

    private const string Worker = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Logging;
        using MintPlayer.Spark.SubscriptionWorker;
        using Raven.Client.Documents;
        using Raven.Client.Documents.Subscriptions;

        namespace TestApp.Workers;

        public class CarEventsWorker : SparkSubscriptionWorker<Car>
        {
            public CarEventsWorker(IDocumentStore s, ILogger<CarEventsWorker> l) : base(s, l) { }
            protected override SubscriptionCreationOptions ConfigureSubscription()
                => new SubscriptionCreationOptions { Query = "from Cars" };
            protected override Task ProcessBatchAsync(SubscriptionBatch<Car> batch, CancellationToken ct)
                => Task.CompletedTask;
        }

        public class Car
        {
            public string? Id { get; set; }
        }
        """;

    [Fact]
    public void SparkSubscriptionWorker_subclass_is_registered()
    {
        var generated = Single(Run([Worker, SubscriptionRuntime], [typeof(SparkSubscriptionWorker<>)]));

        generated.Should().Contain("global::MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionExtensions.AddSubscriptionWorker<global::TestApp.Workers.CarEventsWorker>(builder.Services);");
    }

    [Fact]
    public void Subscription_worker_is_not_emitted_with_only_the_abstractions()
    {
        // Without the runtime package the registration call would not compile, so nothing is emitted.
        Run([Worker], [typeof(SparkSubscriptionWorker<>)]).GeneratedSources.Should().BeEmpty();
    }

    // ── Row policies ──────────────────────────────────────────────────────────────

    private const string RowPolicies = """
        using System;
        using MintPlayer.Spark.Abstractions.Authorization;

        namespace TestApp.Security;

        public sealed class TenantPolicy : IRowPolicy
        {
            public bool AppliesTo(Type entityType) => true;
        }

        public class DraftPolicy : IRowPolicy
        {
            public bool AppliesTo(Type entityType) => true;
        }

        public abstract class PolicyBase : IRowPolicy
        {
            public bool AppliesTo(Type entityType) => true;
        }

        public class GenericPolicy<T> : IRowPolicy
        {
            public bool AppliesTo(Type entityType) => entityType == typeof(T);
        }

        public class Outer
        {
            private class HiddenPolicy : IRowPolicy
            {
                public bool AppliesTo(Type entityType) => true;
            }
        }
        """;

    [Fact]
    public void Row_policies_are_registered_through_AddSparkRowPolicy()
    {
        var generated = Single(Run([RowPolicies], [typeof(IRowPolicy), typeof(SparkBuilderRowPolicyExtensions)]));

        var draft = generated.IndexOf("global::MintPlayer.Spark.Extensions.SparkBuilderRowPolicyExtensions.AddSparkRowPolicy<global::TestApp.Security.DraftPolicy>(builder);", StringComparison.Ordinal);
        var tenant = generated.IndexOf("global::MintPlayer.Spark.Extensions.SparkBuilderRowPolicyExtensions.AddSparkRowPolicy<global::TestApp.Security.TenantPolicy>(builder);", StringComparison.Ordinal);
        draft.Should().BeGreaterThan(-1);
        tenant.Should().BeGreaterThan(draft, "row policies are listed in ordinal order");
        generated.Should().NotContain("PolicyBase");
        generated.Should().NotContain("GenericPolicy");
        generated.Should().NotContain("HiddenPolicy");
    }

    [Fact]
    public void Row_policies_are_not_emitted_without_the_Spark_runtime()
    {
        // IRowPolicy alone (Abstractions) cannot call AddSparkRowPolicy, which lives in MintPlayer.Spark.
        Run([RowPolicies], [typeof(IRowPolicy)]).GeneratedSources.Should().BeEmpty();
    }

    // ── [SparkDiscoverable] contracts ─────────────────────────────────────────────

    [Fact]
    public void Implementations_of_a_SparkDiscoverable_interface_are_registered_with_its_lifetime()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [SparkDiscoverable]
            public interface IReplicaChanged<T> { }

            [SparkDiscoverable(Lifetime = ServiceLifetime.Singleton)]
            public interface IAccountHook { }

            [SparkDiscoverable(Lifetime = ServiceLifetime.Transient)]
            public interface IApprovalDecider { }

            public interface INotDiscoverable { }

            public class Product { }
            public class Order { }

            public class CatalogueChanged : IReplicaChanged<Product>, IReplicaChanged<Order> { }
            public class AuditHook : IAccountHook, INotDiscoverable { }
            public class FirstApprover : IApprovalDecider { }

            public abstract class HookBase : IAccountHook { }
            public class GenericHook<T> : IAccountHook { }
            public class Outer
            {
                private class HiddenHook : IAccountHook { }
            }
            """;

        var generated = Single(Run([source], [typeof(SparkDiscoverableAttribute), typeof(ServiceLifetime)]));

        const string tryAdd = "global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(builder.Services, global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor.";
        generated.Should().Contain(tryAdd + "Scoped<global::TestApp.IReplicaChanged<global::TestApp.Product>, global::TestApp.CatalogueChanged>());");
        generated.Should().Contain(tryAdd + "Scoped<global::TestApp.IReplicaChanged<global::TestApp.Order>, global::TestApp.CatalogueChanged>());");
        generated.Should().Contain(tryAdd + "Singleton<global::TestApp.IAccountHook, global::TestApp.AuditHook>());");
        generated.Should().Contain(tryAdd + "Transient<global::TestApp.IApprovalDecider, global::TestApp.FirstApprover>());");
        generated.Should().NotContain("INotDiscoverable");
        generated.Should().NotContain("HookBase");
        generated.Should().NotContain("GenericHook");
        generated.Should().NotContain("HiddenHook");
    }

    [Fact]
    public void A_SparkDiscoverable_interface_from_a_referenced_package_counts()
    {
        var contracts = GeneratorHarness.CompileToMetadataReference(
            "Acme.Contracts",
            ["""
                using MintPlayer.Spark.Abstractions;

                namespace Acme.Contracts;

                [SparkDiscoverable]
                public interface IOrderApprovalHandler { }
                """],
            [typeof(SparkDiscoverableAttribute)]);

        var source = """
            namespace TestApp;
            public class SendApprovalMail : Acme.Contracts.IOrderApprovalHandler { }
            """;

        var generated = Single(Run([source], [typeof(SparkDiscoverableAttribute)], additionalReferences: [contracts]));

        generated.Should().Contain("ServiceDescriptor.Scoped<global::Acme.Contracts.IOrderApprovalHandler, global::TestApp.SendApprovalMail>());");
    }

    // ── Everything together ───────────────────────────────────────────────────────

    [Fact]
    public void Kinds_are_grouped_in_a_fixed_order()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;
            using MintPlayer.Spark.Abstractions.Authorization;
            using MintPlayer.Spark.Actions;
            using MintPlayer.Spark.Cron;
            using MintPlayer.Spark.Messaging.Abstractions;
            using MintPlayer.Spark.Migrations;

            namespace TestApp;

            // Declared in reverse of the emitted order.
            public class TenantPolicy : IRowPolicy { public bool AppliesTo(Type entityType) => true; }
            public class Seed : ISparkMigration
            {
                public static long Version => 1;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public class Nightly : ISparkCronJob
            {
                public static string CronSchedule => "0 0 * * *";
                public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public record CarSold(string Plate);
            public class CarSoldRecipient : IRecipient<CarSold>
            {
                public Task HandleAsync(CarSold message, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public class ExportAction : ICustomAction
            {
                public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public class Car { public string? Id { get; set; } }
            public class CarActions : DefaultPersistentObjectActions<Car> { }
            """;

        var generated = Single(Run([source],
            [typeof(DefaultPersistentObjectActions<>), typeof(ICustomAction), typeof(IRecipient<>), typeof(ISparkCronJob), typeof(ISparkMigration), typeof(IRowPolicy), typeof(SparkBuilderRowPolicyExtensions)]));

        string[] groups = ["// Actions classes", "// Custom actions", "// Message recipients", "// Cron jobs", "// Migrations", "// Row policies"];
        var previous = -1;
        foreach (var group in groups)
        {
            var at = generated.IndexOf(group, StringComparison.Ordinal);
            at.Should().BeGreaterThan(previous, $"'{group}' follows the group before it");
            previous = at;
        }
        Count(generated, "AddSparkDiscovered(").Should().Be(1, "one entry point, no per-kind methods");
    }
}
