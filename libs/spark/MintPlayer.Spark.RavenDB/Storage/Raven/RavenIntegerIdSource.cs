using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Identity;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// Integer ids on RavenDB (#536 D23 rule 6): RavenDB's native HiLo, with its node tag kept and the whole key
/// lowered (<c>1-a</c>). Spark owns the generator, because <c>ApplySparkConventions</c> replaces the store's
/// document id generator and the store's own HiLo then refuses to run (S-ID1 C13).
/// </summary>
/// <remarks>
/// The ranges are reserved in the store's default database. A key is opaque: sorting by id is alphabetical on
/// RavenDB (<c>people/10-a</c> before <c>people/2-a</c>).
/// </remarks>
[Register(typeof(ISparkIntegerIdSource), ServiceLifetime.Singleton)]
internal sealed partial class RavenIntegerIdSource : ISparkIntegerIdSource, IDisposable
{
    [Inject] private readonly IDocumentStore store;

    private AsyncMultiDatabaseHiLoIdGenerator? generator;

    public async Task<string> NextKeyAsync(Type entityType, CancellationToken cancellationToken)
    {
        generator ??= new AsyncMultiDatabaseHiLoIdGenerator((DocumentStore)store);

        var id = await generator.GenerateDocumentIdAsync(store.Database, entityType);
        var slash = id.IndexOf('/');
        return (slash < 0 ? id : id[(slash + 1)..]).ToLowerInvariant();
    }

    public void Dispose() => generator?.ReturnUnusedRange().GetAwaiter().GetResult();
}
