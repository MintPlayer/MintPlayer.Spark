using MintPlayer.Spark.Abstractions.Links;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary><c>AddEntityRedirects()</c> registers every redirect hook of the project (#602 review).</summary>
public class EntityRedirectRegistrationGeneratorTests
{
    private const string GeneratorName = "EntityRedirectRegistrationGenerator";

    private static readonly Type[] References = [typeof(ISparkEntityRedirect), typeof(SparkBuilderPrerenderingExtensions)];

    private const string Hooks = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using MintPlayer.Spark.Abstractions.Links;

        namespace TestApp.Redirects;

        internal sealed class AccountRedirect : ISparkEntityRedirect
        {
            public string Pattern => "a/{login}";
            public Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => Task.FromResult<SparkEntityTarget?>(null);
        }

        public abstract class RedirectBase : ISparkEntityRedirect
        {
            public abstract string Pattern { get; }
            public Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => Task.FromResult<SparkEntityTarget?>(null);
        }

        public class RepositoryRedirect : RedirectBase
        {
            public override string Pattern => "r/{owner}/{repo}";
        }

        public class Open<T> : RedirectBase
        {
            public override string Pattern => "o/{id}";
        }
        """;

    [Fact]
    public void Registers_every_concrete_closed_hook_including_one_through_a_base_class()
    {
        var result = GeneratorHarness.Run(GeneratorName, [Hooks], referenceTypes: References, rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;
        generated.Should().Contain("AddEntityRedirects");
        generated.Should().Contain("SparkBuilderPrerenderingExtensions.AddEntityRedirect<global::TestApp.Redirects.AccountRedirect>(builder);");
        generated.Should().Contain("AddEntityRedirect<global::TestApp.Redirects.RepositoryRedirect>(builder);");
        generated.Should().NotContain("RedirectBase>", "an abstract class cannot be resolved");
        generated.Should().NotContain("Open<", "an open generic cannot be registered");
    }

    [Fact]
    public void No_source_without_a_hook()
    {
        var result = GeneratorHarness.Run(GeneratorName, ["namespace TestApp; public class Foo { }"], referenceTypes: References, rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }
}
