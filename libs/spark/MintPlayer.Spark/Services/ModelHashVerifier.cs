using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Exceptions;
using System.Text;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Verifies at startup that the deployed model still matches this build's entity classes and that
/// nothing in the model directory has been altered.
///
/// <para>
/// Serving a drifted model is worse than not starting: it surfaces as missing columns and values
/// silently dropped on save, which reads as data loss rather than a configuration mistake. So
/// outside Development a mismatch stops the process before it accepts a request.
/// </para>
///
/// <para>
/// In Development it warns instead. Drift there is the normal state — a developer adds a property
/// and hits F5 — and hard-failing would make the framework hostile during the exact activity it
/// exists to support. Warning rather than staying silent still matters: it is the cheapest early
/// signal if the hash ever stops being deterministic, long before that reaches an operator.
/// </para>
/// </summary>
public static class ModelHashVerifier
{
    /// <summary>
    /// Environment variable carrying an emergency override. It holds the <em>actual</em> model hash
    /// from the error message rather than a boolean, which is what stops it becoming permanent: the
    /// value is specific to one build's model, so the next model change makes it wrong and the
    /// application throws again. A boolean off-switch would be copied into a deployment template and
    /// outlive the incident it was added for.
    /// </summary>
    public const string OverrideVariable = "SPARK_MODEL_HASH_OVERRIDE";

    /// <summary>
    /// The build-time commands write the model; running them must never be blocked by the very
    /// check they exist to satisfy, or a drifted deployment could not be repaired.
    /// </summary>
    private static readonly string[] BuildCommandFlags =
    [
        SparkDevelopmentExtensions.SynchronizeFlag,
        SparkDevelopmentExtensions.VerifyFlag,
    ];

    public static void Verify(
        Type contextType,
        IIndexCatalog indexCatalog,
        string contentRootPath,
        bool isDevelopment,
        Action<string> log)
    {
        if (Environment.GetCommandLineArgs().Any(BuildCommandFlags.Contains))
            return;

        var expected = ModelHashFile.Read(contentRootPath);
        var actual = ModelSynchronizer.BuildModelHashes(contextType, indexCatalog, contentRootPath);

        if (expected is not null && string.Equals(expected.ModelHash, actual.ModelHash, StringComparison.Ordinal))
        {
            // Say so. This used to return in silence, which made "the gate ran and passed" and "the
            // gate never ran" the same observation from outside the process — and this deployment has
            // already had one subsystem be silently dead in production for want of exactly that
            // distinction. An operator reading a startup log can now tell which happened, and the
            // counts turn a vague reassurance into a checkable one.
            log($"Spark model verified: {actual.Entities.Count} entities, {actual.Files.Count} model files" +
                (actual.ConfigFiles is { Count: > 0 } config ? $", {config.Count} config files" : string.Empty) +
                $" ({Short(actual.ModelHash)}).");
            return;
        }

        var message = BuildMessage(expected, actual, contentRootPath);

        var overrideValue = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue)
            && string.Equals(overrideValue.Trim(), actual.ModelHash, StringComparison.OrdinalIgnoreCase))
        {
            // Warned on every startup, never once: an override that stops being visible stops being
            // temporary. Monitoring can alert on this line.
            log($"WARNING: Spark model verification OVERRIDDEN via {OverrideVariable}. The model does not " +
                $"match this build; attributes may be missing and saves may drop values. Run " +
                $"--spark-synchronize-model and remove {OverrideVariable}.");
            return;
        }

        if (isDevelopment)
        {
            log("WARNING: " + message);
            return;
        }

        throw new SparkModelOutOfSyncException(message);
    }

    private static string BuildMessage(ModelHashFile? expected, ModelHashFile actual, string contentRootPath)
    {
        var message = new StringBuilder();

        if (expected is null)
        {
            // Fail closed. Treating a missing file as "nothing to check" would make the whole control
            // bypassable by deleting one file.
            message.AppendLine("Spark cannot verify the model: no readable " + ModelHashFile.FileName + " was found.");
            message.AppendLine();
            message.AppendLine($"Expected it at {ModelHashFile.PathFor(contentRootPath)}.");
            message.AppendLine($"It is generated alongside {SparkAppData.Relative("Model")} and must be deployed with it.");
        }
        else
        {
            message.AppendLine("Spark model is out of sync with this build.");
            message.AppendLine();

            foreach (var line in DescribeDrift(expected, actual))
                message.AppendLine("  " + line);

            message.AppendLine();
            message.AppendLine($"{ModelHashFile.FileName} describes a different model than this build produces,");
            message.AppendLine($"so {SparkAppData.Relative("Model")} no longer matches the entity classes. Attributes may be missing,");
            message.AppendLine("mistyped or read-only, and saves may silently drop values.");
        }

        message.AppendLine();
        message.AppendLine("The application will not start. To fix, regenerate the model and commit the result:");
        message.AppendLine();
        message.AppendLine("    dotnet run --spark-synchronize-model");
        message.AppendLine();
        message.AppendLine($"If this appeared after a deployment rather than a code change, {SparkAppData.RelativeDirectory} was published");
        message.AppendLine("from a different build than the application binaries. Redeploy both from one commit.");
        message.AppendLine();
        message.AppendLine($"To start anyway, set {OverrideVariable} to the actual hash below (this disables");
        message.AppendLine("model verification, and the value stops working at the next model change):");
        message.AppendLine();
        message.AppendLine($"    {OverrideVariable}={actual.ModelHash}");

        return message.ToString();
    }

    /// <summary>
    /// Names what moved. One entity drifting reads as a code change; every entity drifting reads as a
    /// stale <c>App_Data</c> — a distinction a single roll-up hash could not offer, and the first
    /// thing an operator needs at 3am. A composed entry also names the layer that moved (composition
    /// D7): a library update and an edit of the application's own files read differently.
    /// </summary>
    internal static IEnumerable<string> DescribeDrift(ModelHashFile expected, ModelHashFile actual)
    {
        var reported = 0;

        // ConfigFiles is null in a hash file written before it existed. Comparing an absent set
        // against a populated one would report every config file as newly added on the first run
        // after upgrading, which is drift the author did not cause and cannot fix except by
        // re-synchronizing — so an absent set means "not covered yet" rather than "empty".
        var compareConfig = expected.ConfigFiles is not null && actual.ConfigFiles is not null;

        // Layers is null in a version 1 file: there is nothing to say which layer moved.
        var compareLayers = expected.Version >= 2;

        foreach (var line in Compare(expected.Entities, actual.Entities, "entity", static key => key, null, null)
                     .Concat(Compare(expected.Files, actual.Files, "file", ModelFileShape.LayerKey, expected, actual))
                     .Concat(compareConfig
                         ? Compare(expected.ConfigFiles!, actual.ConfigFiles!, "config", static key => key, expected, actual)
                         : [])
                     .Concat(compareLayers ? DescribeLayerOnlyDrift(expected, actual) : [])
                     .Concat(compareLayers ? DescribeLibraryDrift(expected, actual) : []))
        {
            if (reported++ == 12)
            {
                yield return $"… and more; the whole model differs, which usually means a stale {SparkAppData.RelativeDirectory}.";
                yield break;
            }
            yield return line;
        }

        if (reported == 0 && expected.Version < actual.Version)
            yield return $"{ModelHashFile.FileName} is version {expected.Version}, written before the hash recorded the layers each entry came from (version {actual.Version}); synchronize once to record them";
        else if (reported == 0)
            yield return $"model  expected {Short(expected.ModelHash)}  actual {Short(actual.ModelHash)}";
    }

    private static IEnumerable<string> Compare(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual,
        string label,
        Func<string, string> layerKey,
        ModelHashFile? expectedFile,
        ModelHashFile? actualFile)
    {
        foreach (var key in expected.Keys.Concat(actual.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hasExpected = expected.TryGetValue(key, out var expectedHash);
            var hasActual = actual.TryGetValue(key, out var actualHash);
            var expectedLayers = LayersOf(expectedFile, layerKey(key));
            var actualLayers = LayersOf(actualFile, layerKey(key));

            if (!hasExpected)
                yield return $"{label} {key}: {Origin(actualLayers, actualFile, "present on disk")} but not in {ModelHashFile.FileName} (added since the model was generated)";
            else if (!hasActual)
                yield return $"{label} {key}: in {ModelHashFile.FileName}{(expectedLayers is null ? string.Empty : $" ({Origin(expectedLayers, expectedFile, "on disk")})")} but missing (removed since the model was generated)";
            else if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                yield return $"{label} {key}: expected {Short(expectedHash!)}  actual {Short(actualHash!)}{Moved(expectedLayers, actualLayers, expectedFile, actualFile)}";
        }
    }

    /// <summary>A composed entry whose layers moved while the composed structure did not: a library now states what the application's delta already did, or the other way round.</summary>
    private static IEnumerable<string> DescribeLayerOnlyDrift(ModelHashFile expected, ModelHashFile actual)
    {
        var expectedLayers = expected.Layers ?? [];
        var actualLayers = actual.Layers ?? [];
        foreach (var key in expectedLayers.Keys.Concat(actualLayers.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (EntryHash(expected, key) is not { } before || EntryHash(actual, key) is not { } after
                || !string.Equals(before, after, StringComparison.Ordinal))
                continue;   // Added, removed or changed: already reported with its layers.

            expectedLayers.TryGetValue(key, out var was);
            actualLayers.TryGetValue(key, out var now);
            if (MovedLayers(was, now, expected, actual) is { Count: > 0 } moved)
                yield return $"layers of {key}: the composed structure is unchanged, but the layers stating it moved: {string.Join(", ", moved)}";
        }
    }

    private static IEnumerable<string> DescribeLibraryDrift(ModelHashFile expected, ModelHashFile actual)
    {
        var was = expected.Libraries ?? [];
        var now = actual.Libraries ?? [];
        foreach (var alias in was.Keys.Concat(now.Keys).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal))
        {
            var before = was.GetValueOrDefault(alias);
            var after = now.GetValueOrDefault(alias);
            if (before is null)
                yield return $"library {alias} ({after}): newly states a layer of the model, actions or program units";
            else if (after is null)
                yield return $"library {alias} ({before}): no longer states a layer (removed, or its layers were dropped)";
            else if (!string.Equals(before, after, StringComparison.Ordinal))
                yield return $"library {alias}: was {before}, now {after}";
        }
    }

    /// <summary>The composed entry's own hash: a model type's file hash, or a config file's.</summary>
    private static string? EntryHash(ModelHashFile file, string layerKey)
        => layerKey.StartsWith("Model/", StringComparison.Ordinal)
            ? file.Files.GetValueOrDefault(layerKey["Model/".Length..])
            : file.ConfigFiles?.GetValueOrDefault(layerKey);

    private static SortedDictionary<string, string>? LayersOf(ModelHashFile? file, string layerKey)
        => file?.Layers is { } layers && layers.TryGetValue(layerKey, out var entry) ? entry : null;

    /// <summary>Where an entry comes from: the libraries that state it (M4's message said "present on disk" for a library type too), else <paramref name="appOnly"/>.</summary>
    private static string Origin(SortedDictionary<string, string>? layers, ModelHashFile? file, string appOnly)
    {
        var libraries = layers?.Keys.Where(k => k != SparkLayerProvenance.App).ToList() ?? [];
        if (libraries.Count == 0) return appOnly;
        var shipped = $"shipped by {string.Join(", ", libraries.Select(a => Name(a, file)))}";
        return layers!.ContainsKey(SparkLayerProvenance.App) ? $"{shipped}, with the application's delta" : shipped;
    }

    /// <summary><c> — moved: authorization (MintPlayer.Spark.Authorization)</c>, naming every layer whose own hash changed, appeared or disappeared; empty when none is known to have.</summary>
    private static string Moved(SortedDictionary<string, string>? expected, SortedDictionary<string, string>? actual, ModelHashFile? expectedFile, ModelHashFile? actualFile)
        => MovedLayers(expected, actual, expectedFile, actualFile) is { Count: > 0 } moved
            ? " — layer " + string.Join(", ", moved)
            : string.Empty;

    private static List<string> MovedLayers(SortedDictionary<string, string>? expected, SortedDictionary<string, string>? actual, ModelHashFile? expectedFile, ModelHashFile? actualFile)
    {
        if (expectedFile is null || actualFile is null || expectedFile.Version < 2) return [];
        expected ??= [];
        actual ??= [];

        var moved = new List<string>();
        foreach (var layer in expected.Keys.Concat(actual.Keys).Distinct(StringComparer.Ordinal).OrderBy(l => l == SparkLayerProvenance.App ? 1 : 0).ThenBy(l => l, StringComparer.Ordinal))
        {
            var had = expected.TryGetValue(layer, out var before);
            var has = actual.TryGetValue(layer, out var after);
            var name = Name(layer, had ? expectedFile : actualFile);
            if (had && has && !string.Equals(before, after, StringComparison.Ordinal)) moved.Add($"{name} changed");
            else if (!had && has && (expected.Count > 0 || layer != SparkLayerProvenance.App)) moved.Add($"{name} added");
            else if (had && !has) moved.Add($"{name} removed");
        }
        return moved;
    }

    /// <summary>A layer by its alias and, for a library, its assembly: <c>authorization (MintPlayer.Spark.Authorization)</c>.</summary>
    private static string Name(string layer, ModelHashFile? file)
    {
        if (layer == SparkLayerProvenance.App) return "the application's file";
        return file?.Libraries is { } libraries && libraries.TryGetValue(layer, out var assembly)
            ? $"library '{layer}' ({assembly})"
            : $"library '{layer}'";
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12] + "…";
}
