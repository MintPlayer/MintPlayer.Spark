using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Tests.Architecture;

/// <summary>
/// Spark calls application-authored code with <c>BindingFlags.DoNotWrapExceptions</c>, everywhere.
/// </summary>
/// <remarks>
/// <para>
/// Without it, <c>MethodBase.Invoke</c> wraps whatever the target throws in a
/// <c>TargetInvocationException</c> and <b>no typed <c>catch</c> in the framework matches</b>. A hook
/// refusing politely with <c>SparkValidationException</c> reaches the caller as a 500 with its message
/// lost; one raising <c>SparkRetryActionException</c> never becomes a 449.
/// </para>
/// <para>
/// ⚠️ <b>This test exists because the omission was found three times in one afternoon</b>, on
/// <c>RefreshInvoker</c>, on all five sites in <c>DatabaseAccess</c>, and in <c>QueryExecutor</c> —
/// each time by a symptom miles from the cause, and each time after the previous one had been
/// "fixed". An audit then turned up eight more. A rule that has to be remembered at every new call
/// site is not a rule; this is what makes it one.
/// </para>
/// <para>
/// It survives on a property that makes the omission nearly invisible: the wrapping only happens when
/// the target throws <i>before</i> its <c>Task</c> exists. An <c>async</c> override's exception lands
/// on the returned task and <c>await</c> rethrows it unwrapped, so the ordinary case looks correct.
/// A <b>non-async</b> override that validates and refuses up front does not — the shape of a guard
/// clause, and therefore the shape most likely to be written.
/// </para>
/// <para>
/// ⚠️ <b>Deliberately not in scope: <c>MintPlayer.Spark.Messaging</c>.</b> Its
/// <c>MessageProcessor</c> invokes recipients without the flag <i>on purpose</i> — it catches
/// <c>TargetInvocationException</c> explicitly and unwraps <c>InnerException</c>, including a separate
/// non-retryable clause keyed on it. The rule is "a typed catch must be able to match", and there one
/// can. Applying the flag mechanically would make working code dead.
/// </para>
/// </remarks>
public class HookInvocationTests
{
    /// <summary>The framework source that reflects into application code.</summary>
    private const string ScannedDirectory = @"libs\spark\MintPlayer.Spark";

    /// <summary>The one file allowed to name the flag — it defines the constant.</summary>
    private const string Definition = "SparkHookInvocation.cs";

    /// <summary>
    /// The receiver names that mean "an application's actions class". Anything invoked on one of
    /// these is a hook; anything else in this codebase is a Raven or BCL generic, which cannot throw
    /// a Spark exception and does not need the flag.
    /// </summary>
    private static readonly Regex HookCall = new(
        @"\b(?:actions|actionsInstance|recipientInstance)\s*,",
        RegexOptions.Compiled);

    [Fact]
    public void Every_reflective_call_into_an_actions_instance_unwraps_its_exceptions()
    {
        var root = RepositoryRoot();
        var directory = Path.Combine(root, ScannedDirectory);
        Directory.Exists(directory).Should().BeTrue(
            $"'{ScannedDirectory}' must exist, or this test silently passes by scanning nothing");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == Definition)
                continue;

            // Generated output is not hand-written and is not where this rule is kept.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith('*'))
                    continue;

                // `.Invoke(` on a delegate (configure?.Invoke(options)) is not reflection and has no
                // flag to pass. The receiver-name check below is what separates the two.
                if (!line.Contains(".Invoke(", StringComparison.Ordinal))
                    continue;

                var call = line[(line.IndexOf(".Invoke(", StringComparison.Ordinal) + ".Invoke(".Length)..];
                if (!HookCall.IsMatch(call))
                    continue;

                if (!line.Contains("HookInvoke", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {trimmed}");
            }
        }

        offenders.Should().BeEmpty(
            "a reflective call into an actions instance must pass HookInvoke, or everything the hook "
            + "throws arrives wrapped in TargetInvocationException and no typed catch matches it");
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution. ⚠️ A wrong answer makes
    /// the test vacuous rather than red, which is why the scan asserts its directory exists first.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
