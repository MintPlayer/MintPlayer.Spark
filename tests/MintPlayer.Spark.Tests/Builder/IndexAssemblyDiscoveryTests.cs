using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Index and projection discovery used to scan only the entry assembly, so a module shipped as a
/// class library got its indexes never created and its projections never registered — and an
/// unregistered projection means queries silently return index-computed fields as null.
///
/// <para>
/// These pin the declaration surface and, most importantly, that the entry assembly is never lost:
/// a module declaring its own assembly must not displace the application's own indexes.
/// </para>
/// </summary>
public class IndexAssemblyDiscoveryTests
{
    private static Assembly EntryAssembly => Assembly.GetEntryAssembly()!;

    [Fact]
    public void With_nothing_declared_only_the_entry_assembly_is_scanned()
    {
        // The back-compatibility guarantee: an application that declares nothing behaves exactly as
        // it did before the declaration surface existed.
        var registry = new SparkModuleRegistry();

        registry.ResolveIndexAssemblies().Should().Equal([EntryAssembly]);
    }

    [Fact]
    public void A_declared_assembly_is_appended_to_the_entry_assembly_not_substituted_for_it()
    {
        // Substituting is the tempting shape, and it silently drops the application's own indexes
        // the moment it adds a module that declares one.
        var registry = new SparkModuleRegistry();
        var declared = typeof(SparkModuleRegistry).Assembly;

        registry.AddIndexAssembly(declared);

        var resolved = registry.ResolveIndexAssemblies();
        resolved.Should().HaveCount(2);
        resolved[0].Should().BeSameAs(EntryAssembly, "the entry assembly stays first and is never lost");
        resolved.Should().Contain(declared);
    }

    [Fact]
    public void Declaring_the_same_assembly_twice_is_idempotent()
    {
        // Two modules may legitimately ship from one assembly, and an app may declare what a module
        // already did. Neither should cost a duplicate scan.
        var registry = new SparkModuleRegistry();
        var declared = typeof(SparkModuleRegistry).Assembly;

        registry.AddIndexAssembly(declared);
        registry.AddIndexAssembly(declared);

        registry.ResolveIndexAssemblies().Should().HaveCount(2);
    }

    [Fact]
    public void Declaring_the_entry_assembly_explicitly_does_not_duplicate_it()
    {
        var registry = new SparkModuleRegistry();

        registry.AddIndexAssembly(EntryAssembly);

        registry.ResolveIndexAssemblies().Should().Equal([EntryAssembly]);
    }

    [Fact]
    public void A_null_assembly_is_rejected_rather_than_silently_ignored()
    {
        var registry = new SparkModuleRegistry();

        var act = () => registry.AddIndexAssembly(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_builder_extension_records_the_declaration_on_the_registry()
    {
        // What a module actually calls from inside its own AddXxx(...), so consumers write no code.
        using var scratch = new ScratchServices();
        var declared = typeof(SparkModuleRegistry).Assembly;

        scratch.Builder.AddIndexesFrom(declared);

        scratch.Builder.Registry.ResolveIndexAssemblies().Should().Contain(declared);
    }

    [Fact]
    public void The_marker_type_overload_declares_the_types_own_assembly()
    {
        using var scratch = new ScratchServices();

        scratch.Builder.AddIndexesFromAssemblyContaining<SparkModuleRegistry>();

        scratch.Builder.Registry.ResolveIndexAssemblies()
            .Should().Contain(typeof(SparkModuleRegistry).Assembly);
    }

    /// <summary>Minimal <see cref="ISparkBuilder"/> so the extensions can be exercised without a host.</summary>
    private sealed class ScratchServices : IDisposable
    {
        public ScratchServices() => Builder = new StubBuilder();

        public StubBuilder Builder { get; }

        public void Dispose() { }

        internal sealed class StubBuilder : ISparkBuilder
        {
            public IServiceCollection Services { get; } = new ServiceCollection();
            public IConfiguration? Configuration => null;
            public SparkModuleRegistry Registry { get; } = new();
        }
    }
}
