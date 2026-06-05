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
