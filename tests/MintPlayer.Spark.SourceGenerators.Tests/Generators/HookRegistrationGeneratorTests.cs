using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>#482: <c>AddHooks</c> registers every concrete persistence hook the project declares.</summary>
public class HookRegistrationGeneratorTests
{
    private const string GeneratorName = "HookRegistrationGenerator";

    [Fact]
    public void Hooks_are_registered_and_Actions_classes_are_not()
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

            // Run as its type's hook without registration: never in AddHooks.
            public class CarActions : DefaultPersistentObjectActions<Car>, IBeforeDelete<Car>
            {
                public CarActions(IEntityMapper mapper) : base(mapper) { }
                public ValueTask OnBeforeDeleteAsync(Car entity, DeleteContext context) => ValueTask.CompletedTask;
            }

            public abstract class AbstractHook : IAfterLoad
            {
                public bool AppliesTo(Type entityType) => true;
                public ValueTask OnAfterLoadAsync(LoadContext context) => ValueTask.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkHook), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddHooks(");
        generated.Should().Contain("SparkBuilderHookExtensions.AddHook<global::TestApp.CarAudit>(builder);");
        generated.Should().Contain("SparkBuilderHookExtensions.AddHook<global::TestApp.Stamping>(builder);");
        generated.Should().NotContain("CarActions");
        generated.Should().NotContain("AbstractHook");
    }

    [Fact]
    public void No_source_without_hooks()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkHook)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }
}
