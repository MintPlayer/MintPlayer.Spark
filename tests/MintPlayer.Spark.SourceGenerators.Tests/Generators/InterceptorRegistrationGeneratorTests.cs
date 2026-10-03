using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>#482: <c>AddInterceptors</c> registers every concrete persistence interceptor the project declares.</summary>
public class InterceptorRegistrationGeneratorTests
{
    private const string GeneratorName = "InterceptorRegistrationGenerator";

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

            // Run as its type's interceptor without registration: never in AddInterceptors.
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
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkInterceptor), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddInterceptors(");
        generated.Should().Contain("SparkBuilderInterceptorExtensions.AddInterceptor<global::TestApp.CarAudit>(builder);");
        generated.Should().Contain("SparkBuilderInterceptorExtensions.AddInterceptor<global::TestApp.Stamping>(builder);");
        generated.Should().NotContain("CarActions");
        generated.Should().NotContain("AbstractInterceptor");
    }

    [Fact]
    public void No_source_without_interceptors()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkInterceptor)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }
}
