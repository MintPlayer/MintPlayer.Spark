using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Files.Images;

public static class SparkImageVariantCommand
{
    internal const string Flag = "--spark-regenerate-variants";
    internal const string ForceFlag = "--force";

    /// <summary>
    /// <c>--spark-regenerate-variants [--force]</c> (#700 M8): makes the variants of every stored image whose variants do not
    /// match the configuration any more, prints a summary and reports that the host should exit. <c>--force</c> remakes every
    /// image's variants. It needs the database and the store, so it runs on the built app, before <c>app.Run()</c>. The exit
    /// code is 1 when an image could not be processed (it is left as it was, and logged).
    /// </summary>
    /// <returns><see langword="true"/> when the command ran and <c>Main</c> should return.</returns>
    /// <example><code>
    /// var app = builder.Build();
    /// if (await app.RegenerateSparkImageVariantsIfRequested(args))
    ///     return;
    /// </code></example>
    public static async Task<bool> RegenerateSparkImageVariantsIfRequested(this WebApplication app, string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);
        if (!args.Contains(Flag))
            return false;

        var result = await app.Services.GetRequiredService<ImageVariantRegenerator>().RunAsync(args.Contains(ForceFlag), cancellationToken);
        Console.WriteLine($"Image variants: {result.Images} image(s) looked at, {result.Regenerated} regenerated, {result.Failed} failed.");
        if (result.Failed > 0)
            Environment.ExitCode = 1;
        return true;
    }
}
