using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Commands;

namespace MintPlayer.Spark.Files.Images;

/// <summary>
/// <c>--spark-regenerate-variants [--force]</c> (#700 M8, an <see cref="ISparkCommand"/> since #536 D34, registered by
/// <c>AddFiles</c>): makes the variants of every stored image whose variants do not match the configuration any more and
/// prints a summary. <c>--force</c> remakes every image's variants. It needs the database and the store. The exit code is
/// 1 when an image could not be processed (it is left as it was, and logged).
/// </summary>
internal sealed partial class SparkImageVariantCommand : ISparkCommand
{
    internal const string Flag = "--spark-regenerate-variants";
    internal const string ForceFlag = "--force";

    [Inject] private readonly ImageVariantRegenerator regenerator;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await regenerator.RunAsync(args.Contains(ForceFlag), cancellationToken);
        Console.WriteLine($"Image variants: {result.Images} image(s) looked at, {result.Regenerated} regenerated, {result.Failed} failed.");
        return result.Failed > 0 ? 1 : 0;
    }
}
