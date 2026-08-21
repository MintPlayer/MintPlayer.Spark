using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// A merge-queue gate over the anonymous surface, mirroring <c>--spark-verify-model</c>.
/// <para>
/// <c>security.json</c> is a data file: widening it is a one-line diff that reads no differently
/// from narrowing it, and the consequence is invisible until someone reaches the endpoint. Committing
/// a baseline turns "who can reach this without signing in" into something a reviewer sees change.
/// </para>
/// <para>
/// Computed from configuration alone, so it runs in CI with no RavenDB — the same property that lets
/// the model commands run there.
/// </para>
/// </summary>
public static class SparkSecurityVerificationExtensions
{
    internal const string VerifyFlag = "--spark-verify-security";
    internal const string SynchronizeFlag = "--spark-synchronize-security";

    /// <summary>The committed baseline, beside the model's hash file for the same reason.</summary>
    internal const string BaselineFile = "App_Data/securityPosture.txt";

    private const int ExitMisconfigured = 2;
    private const int ExitDrift = 3;

    /// <summary>
    /// Handles the security-posture commands and reports whether the host should stop instead of
    /// starting.
    /// <list type="bullet">
    /// <item><c>--spark-synchronize-security</c> writes the baseline.</item>
    /// <item><c>--spark-verify-security</c> writes nothing and exits 3 if the anonymous surface has
    /// changed.</item>
    /// </list>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a command was handled and the host should return from
    /// <c>Main</c>; <see langword="false"/> when neither flag was passed.
    /// </returns>
    /// <example>
    /// <code>
    /// if (builder.VerifySparkSecurityIfRequested(args))
    ///     return;
    /// </code>
    /// </example>
    public static bool VerifySparkSecurityIfRequested(this WebApplicationBuilder builder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        var verifyOnly = args.Contains(VerifyFlag);
        if (!verifyOnly && !args.Contains(SynchronizeFlag))
            return false;

        // Only the reporter is resolved, so nothing that would need a database is ever constructed.
        using var provider = builder.Services.BuildServiceProvider();
        var reporter = provider.GetService<ISecurityPostureReporter>();

        if (reporter is null)
        {
            Console.Error.WriteLine(
                "Spark: no security posture to report — this application registers no authorization "
                + "model. Call spark.AddAuthorization() if it should have one.");
            Environment.ExitCode = ExitMisconfigured;
            return true;
        }

        var path = Path.Combine(builder.Environment.ContentRootPath, BaselineFile);
        var current = Render(reporter.Describe());

        if (verifyOnly)
            Verify(path, current);
        else
            Synchronize(path, current);

        return true;
    }

    private static void Synchronize(string path, string current)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, current);
        Console.WriteLine($"Spark: wrote the anonymous-surface baseline to {BaselineFile}.");
    }

    private static void Verify(string path, string current)
    {
        // A missing baseline is drift, not a pass. Treating it as "nothing to compare against" would
        // make deleting the file the way to silence the gate.
        var committed = File.Exists(path) ? File.ReadAllText(path) : null;

        if (string.Equals(NormalizeLineEndings(committed), NormalizeLineEndings(current), StringComparison.Ordinal))
            return;

        Console.Error.WriteLine("Spark: the set of rights reachable without signing in has changed.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Committed:");
        Console.Error.WriteLine(Indent(committed ?? "(no baseline committed)"));
        Console.Error.WriteLine("Current:");
        Console.Error.WriteLine(Indent(current));
        Console.Error.WriteLine(
            $"If this is intended, run '{SynchronizeFlag}' and commit {BaselineFile} so the change is "
            + "reviewed rather than discovered.");

        Environment.ExitCode = ExitDrift;
    }

    /// <summary>
    /// Deliberately plain text rather than JSON: the file exists to be read in a pull request, and a
    /// one-right-per-line diff says what changed without a reviewer parsing anything.
    /// </summary>
    private static string Render(SecurityPosture posture)
        => posture.AnonymouslyReachable.Count == 0
            ? "(nothing)\n"
            : string.Join("\n", posture.AnonymouslyReachable) + "\n";

    private static string? NormalizeLineEndings(string? value)
        => value?.Replace("\r\n", "\n").TrimEnd('\n');

    private static string Indent(string value)
        => string.Join("\n", value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => "  " + l));
}
