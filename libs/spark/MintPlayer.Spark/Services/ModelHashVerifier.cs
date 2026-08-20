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
            return;

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
            message.AppendLine("It is generated alongside App_Data/Model and must be deployed with it.");
        }
        else
        {
            message.AppendLine("Spark model is out of sync with this build.");
            message.AppendLine();

            foreach (var line in DescribeDrift(expected, actual))
                message.AppendLine("  " + line);

            message.AppendLine();
            message.AppendLine($"{ModelHashFile.FileName} describes a different model than this build produces,");
            message.AppendLine("so App_Data/Model no longer matches the entity classes. Attributes may be missing,");
            message.AppendLine("mistyped or read-only, and saves may silently drop values.");
        }

        message.AppendLine();
        message.AppendLine("The application will not start. To fix, regenerate the model and commit the result:");
        message.AppendLine();
        message.AppendLine("    dotnet run --spark-synchronize-model");
        message.AppendLine();
        message.AppendLine("If this appeared after a deployment rather than a code change, App_Data was published");
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
    /// thing an operator needs at 3am.
    /// </summary>
    internal static IEnumerable<string> DescribeDrift(ModelHashFile expected, ModelHashFile actual)
    {
        var reported = 0;

        foreach (var line in Compare(expected.Entities, actual.Entities, "entity")
                     .Concat(Compare(expected.Files, actual.Files, "file")))
        {
            if (reported++ == 12)
            {
                yield return "… and more; the whole model differs, which usually means a stale App_Data.";
                yield break;
            }
            yield return line;
        }

        if (reported == 0)
            yield return $"model  expected {Short(expected.ModelHash)}  actual {Short(actual.ModelHash)}";
    }

    private static IEnumerable<string> Compare(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual,
        string label)
    {
        foreach (var key in expected.Keys.Concat(actual.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hasExpected = expected.TryGetValue(key, out var expectedHash);
            var hasActual = actual.TryGetValue(key, out var actualHash);

            if (!hasExpected)
                yield return $"{label} {key}: present on disk but not in {ModelHashFile.FileName} (added since the model was generated)";
            else if (!hasActual)
                yield return $"{label} {key}: in {ModelHashFile.FileName} but missing (removed since the model was generated)";
            else if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                yield return $"{label} {key}: expected {Short(expectedHash!)}  actual {Short(actualHash!)}";
        }
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12] + "…";
}
