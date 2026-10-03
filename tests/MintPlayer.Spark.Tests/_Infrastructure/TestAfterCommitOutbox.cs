using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Interceptors;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// The durable after-commit outbox (#482, D17) without the Messaging runtime: each unit of work is
/// stored as a document in the write's own session, exactly as Messaging stores its message, and
/// <see cref="DrainAsync"/> runs the committed ones through the framework's dispatcher. A refused or
/// cancelled write commits no document, so its interceptors never run — the property under test.
/// </summary>
/// <remarks>Register with <see cref="AddTestAfterCommitOutbox"/>; singleton state, scoped outbox.</remarks>
public sealed class TestAfterCommitOutbox
{
    private readonly ConcurrentQueue<string> pending = new();

    /// <summary>The ids of every unit of work stored so far — committed or not.</summary>
    public IReadOnlyCollection<string> Enqueued => pending;

    internal void Add(string id) => pending.Enqueue(id);

    /// <summary>
    /// Runs every committed unit of work, oldest first, each in its own scope (as Messaging would), and
    /// forgets the ones that never committed. Returns how many ran.
    /// </summary>
    public async Task<int> DrainAsync(IServiceProvider services)
    {
        var ran = 0;
        var store = services.GetRequiredService<IDocumentStore>();
        while (pending.TryDequeue(out var id))
        {
            SparkAfterCommitWork? work;
            using (var session = store.OpenAsyncSession())
                work = (await session.LoadAsync<Stored>(id))?.Work;
            if (work is null)
                continue;

            using var scope = services.CreateScope();
            var dispatched = await scope.ServiceProvider.GetRequiredService<ISparkAfterCommitDispatcher>().DispatchAsync(work, CancellationToken.None);
            if (!dispatched)
                throw new InvalidOperationException($"After-commit interceptor '{work.InterceptorType}' was not found.");
            ran++;
        }
        return ran;
    }

    internal sealed class Stored
    {
        public string? Id { get; set; }
        public SparkAfterCommitWork? Work { get; set; }
    }
}

internal sealed class TestAfterCommitOutboxSeam(TestAfterCommitOutbox state) : ISparkAfterCommitOutbox
{
    public async Task EnqueueAsync(object session, SparkAfterCommitWork work, CancellationToken cancellationToken = default)
    {
        var id = $"TestOutbox/{Guid.NewGuid():N}";
        await ((IAsyncDocumentSession)session).StoreAsync(new TestAfterCommitOutbox.Stored { Work = work }, id, cancellationToken);
        state.Add(id);
    }
}

public static class TestAfterCommitOutboxExtensions
{
    public static IServiceCollection AddTestAfterCommitOutbox(this IServiceCollection services)
    {
        services.AddSingleton<TestAfterCommitOutbox>();
        services.AddScoped<ISparkAfterCommitOutbox, TestAfterCommitOutboxSeam>();
        return services;
    }
}
