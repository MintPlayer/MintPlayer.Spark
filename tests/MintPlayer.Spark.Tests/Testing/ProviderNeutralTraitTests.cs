using System.Reflection;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.IdentityProvider;
using Raven.TestDriver;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>
/// #536 M9: CI's SQL Server and PostgreSQL legs run this assembly with <c>--filter SparkDatabase=Any</c>
/// (<see cref="SparkTestTraits"/>). A provider-neutral class without the trait would silently never run on SQL, and a
/// RavenDB-only class with it would run on RavenDB a second time there. A class is provider-neutral when its database
/// comes from <see cref="SparkTestDatabases"/>: a neutral driver, <see cref="SparkSharedDatabase"/>,
/// <see cref="SparkSharedHost{TContext}"/> or <see cref="OidcTestHost"/>.
/// </summary>
public class ProviderNeutralTraitTests
{
    private static readonly Type[] TestClasses = typeof(ProviderNeutralTraitTests).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetMethods().Any(m => m.IsDefined(typeof(FactAttribute), inherit: true)))
        .ToArray();

    [Fact]
    public void Every_provider_neutral_class_carries_the_trait()
    {
        TestClasses.Where(t => IsProviderNeutral(t) && !HasAnyTrait(t)).Select(t => t.FullName).Order().ToList()
            .Should().BeEmpty("a provider-neutral class needs [Trait(SparkTestTraits.Database, SparkTestTraits.Any)], or the SQL legs leave it out");
    }

    [Fact]
    public void No_RavenDB_only_class_carries_the_trait()
    {
        TestClasses.Where(t => IsRavenOnly(t) && HasAnyTrait(t)).Select(t => t.FullName).Order().ToList()
            .Should().BeEmpty("a RavenDB-only class with the trait runs on RavenDB again in every SQL leg");
    }

    private static bool IsProviderNeutral(Type type)
        => typeof(SparkTestDriver).IsAssignableFrom(type)
        || typeof(SparkSharedTestDriver).IsAssignableFrom(type)
        || typeof(OidcTestHost).IsAssignableFrom(type)
        || Dependencies(type).Any(d => d == typeof(SparkSharedDatabase) || DerivesFromGeneric(d, typeof(SparkSharedHost<>)));

    private static bool IsRavenOnly(Type type)
        => typeof(RavenTestDriver).IsAssignableFrom(type)
        || Dependencies(type).Any(typeof(RavenTestDriver).IsAssignableFrom);

    /// <summary>What xUnit hands the class: its constructor parameters and its class and collection fixtures.</summary>
    private static IEnumerable<Type> Dependencies(Type type)
        => type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Concat(type.GetInterfaces()
                .Where(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IClassFixture<>) || i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)))
                .Select(i => i.GetGenericArguments()[0]));

    private static bool DerivesFromGeneric(Type type, Type definition)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == definition)
                return true;
        return false;
    }

    /// <summary>The trait on the class or a base class, which is where xUnit reads it from too.</summary>
    private static bool HasAnyTrait(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetCustomAttributesData().Any(a => a.AttributeType == typeof(TraitAttribute)
                    && a.ConstructorArguments is [{ Value: SparkTestTraits.Database }, { Value: SparkTestTraits.Any }]))
                return true;
        return false;
    }
}
