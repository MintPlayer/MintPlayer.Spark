using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class ActionsRegistrationGeneratorTests
{
    // Loaded dynamically — can't use `nameof` because the generator DLL isn't referenced at compile time.
    private const string GeneratorName = "ActionsRegistrationGenerator";

    [Fact]
    public void No_actions_classes_produces_no_source()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Single_actions_class_emits_AddActions_extension()
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

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var (hintName, generated) = result.GeneratedSources[0];

        hintName.Should().EndWith(".g.cs");
        generated.Should().Contain("internal static class SparkActionsBuilderExtensions");
        generated.Should().Contain("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddActions");
        generated.Should().Contain("AddSparkActions<global::TestApp.Actions.WidgetActions, global::TestApp.Actions.Widget>");
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

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("AddSparkActions<global::TestApp.Actions.CarActions, global::TestApp.Actions.Car>");
        generated.Should().Contain("AddSparkActions<global::TestApp.Actions.PersonActions, global::TestApp.Actions.Person>");
    }

    /// <summary>A compiled "library" shipping <c>WidgetActions</c> for <c>Widget</c>, the shape of MintPlayer.Spark.Logging's LogActions.</summary>
    internal static Microsoft.CodeAnalysis.MetadataReference WidgetLibrary() => GeneratorHarness.CompileToMetadataReference(
        "WidgetLibrary",
        ["""
        using MintPlayer.Spark.Abstractions;
        using MintPlayer.Spark.Actions;

        namespace WidgetLibrary;

        public class Widget { }

        public partial class WidgetActions : DefaultPersistentObjectActions<Widget>
        {
            public WidgetActions() : base(null!) { }
        }
        """],
        referenceTypes: LibraryReferences);

    /// <summary>What compiling an actions class binds to: its base ctor's optional <c>IHttpContextAccessor</c> included.</summary>
    internal static readonly Type[] LibraryReferences =
        [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>), typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor)];

    [Fact]
    public void An_app_class_specialising_a_library_actions_class_of_the_same_name_is_recorded_as_its_override()
    {
        // #660 G17: the resolver finds both WidgetActions by name; the attribute tells it the app's one wins.
        var source = """
            namespace TestApp.Actions;

            public partial class WidgetActions : WidgetLibrary.WidgetActions { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [.. LibraryReferences, typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection)],
            rootNamespace: "TestApp",
            additionalReferences: [WidgetLibrary()]);

        var generated = result.GeneratedSources.Should().ContainSingle().Which.Source;
        generated.Should().Contain("[assembly: global::MintPlayer.Spark.Abstractions.SparkActionsOverride(typeof(global::WidgetLibrary.Widget), typeof(global::TestApp.Actions.WidgetActions))]");
        result.FinalCompilationDiagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Should().BeEmpty("the emitted attribute must compile");
    }

    [Fact]
    public void A_differently_named_subclass_is_not_recorded_as_an_override()
    {
        var source = """
            namespace TestApp.Actions;

            public partial class SpecialWidgetActions : WidgetLibrary.WidgetActions { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp",
            additionalReferences: [WidgetLibrary()]);

        result.GeneratedSources.Should().ContainSingle().Which.Source.Should().NotContain("SparkActionsOverride");
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

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty("abstract classes shouldn't be registered in DI");
    }

    [Fact]
    public void Generator_produces_nothing_when_Spark_is_not_referenced()
    {
        // Source compiles on its own (no Spark refs) — the generator's knowsSparkProvider
        // should return false and emit nothing.
        var source = """
            namespace TestApp;

            public class Foo
            {
                public int Bar { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: Array.Empty<Type>(),
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }
}
