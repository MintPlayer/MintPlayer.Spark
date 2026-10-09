using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Dependencies;

/// <summary>Runs one queued manifest scan. All the logic is in <see cref="IRepositoryManifestScanner"/>.</summary>
public partial class ScanRepositoryManifestsRecipient : IRecipient<ScanRepositoryManifestsMessage>
{
    [Inject] private readonly IRepositoryManifestScanner scanner;

    public Task HandleAsync(ScanRepositoryManifestsMessage message, CancellationToken cancellationToken = default)
        => scanner.ScanAsync(message.RepositoryId, cancellationToken);
}
