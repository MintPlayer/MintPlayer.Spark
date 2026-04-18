using Raven.Client.Documents;
using Raven.TestDriver;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// xUnit-friendly base class for Spark tests that need an in-memory RavenDB instance.
/// Implements <see cref="IAsyncLifetime"/> so setup and disposal run per test class
/// (use xUnit's default behavior — one instance per test method).
/// </summary>
public abstract class SparkTestDriver : RavenTestDriver, IAsyncLifetime
{
    protected IDocumentStore Store { get; private set; } = null!;

    public virtual Task InitializeAsync()
    {
        Store = GetDocumentStore();
        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        Store.Dispose();
        return Task.CompletedTask;
    }
}
