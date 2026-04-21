using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Convenience subclass that pins <see cref="SparkEndpointFactory{TContext}"/> to
/// <see cref="TestSparkContext"/> so existing endpoint tests can keep using
/// <c>new SparkEndpointFactory(Store, [...])</c> without naming the generic argument.
/// New tests (in this or downstream projects) can instantiate
/// <see cref="SparkEndpointFactory{TContext}"/> directly with their own context type.
/// </summary>
public sealed class SparkEndpointFactory : SparkEndpointFactory<TestSparkContext>
{
    public SparkEndpointFactory(IDocumentStore testStore, EntityTypeFile[] models, Action<IServiceCollection>? configureServices = null)
        : base(testStore, models, configureServices)
    {
    }
}

/// <summary>
/// Minimal SparkContext for endpoint tests. Tests that need additional collections
/// can subclass this or write their own and use <see cref="SparkEndpointFactory{TContext}"/> directly.
/// </summary>
public class TestSparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}

public sealed class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public sealed class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
