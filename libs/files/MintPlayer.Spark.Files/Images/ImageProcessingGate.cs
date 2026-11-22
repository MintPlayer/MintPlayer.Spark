using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Files.Images;

/// <summary>Bounds concurrent image decodes per instance (<see cref="SparkFilesOptions.MaxConcurrentImageProcessing"/>).</summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
internal sealed partial class ImageProcessingGate : IDisposable
{
    [Inject] private readonly IOptions<SparkFilesOptions> options;

    private SemaphoreSlim? semaphore;

    private SemaphoreSlim Semaphore
    {
        get
        {
            if (semaphore is null)
            {
                var count = Math.Max(1, options.Value.MaxConcurrentImageProcessing);
                Interlocked.CompareExchange(ref semaphore, new SemaphoreSlim(count, count), null);
            }
            return semaphore;
        }
    }

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        var gate = Semaphore;
        await gate.WaitAsync(cancellationToken);
        return new Release(gate);
    }

    public void Dispose() => semaphore?.Dispose();

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                gate.Release();
        }
    }
}
