using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using System.Text;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// A merge-queue gate over the effective rights, mirroring <c>--spark-verify-model</c>.
/// <para>
/// The baseline is the readable table of every effective right with the layer it came from
/// (composition D4), the anonymous surface first. A library that ships rights, or an update that
/// changes them, is reviewed as a diff of that file. Hashing it with layer provenance belongs to M7 (D7).
/// </para>
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
    internal static string BaselineFile => SparkAppData.Relative(BaselineFileName);

    private const string BaselineFileName = "securityPosture.txt";

    private const int ExitMisconfigured = 2;
    private const int ExitDrift = 3;

    /// <summary>
    /// Handles the security-posture commands and reports whether the host should stop instead of
    /// starting.
    /// <list type="bullet">
    /// <item><c>--spark-synchronize-security</c> writes the baseline.</item>
    /// <item><c>--spark-verify-security</c> writes nothing and exits 3 if the posture has changed (with
    /// a warning of its own when the anonymous surface did), 2 if the configuration does not compose.</item>
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
            // Unreachable in an application that called AddSpark, which registers the reporter
            // unconditionally. Kept because reaching it means the container was built without
            // Spark at all, and exiting 2 says so rather than throwing a null reference.
            Console.Error.WriteLine(
                "Spark: no security posture to report — AddSpark has not run, so there is no "
                + "authorization model in the container.");
            Environment.ExitCode = ExitMisconfigured;
            return true;
        }

        var path = SparkAppData.Path(builder.Environment.ContentRootPath, BaselineFileName);
        string current;
        try
        {
            current = Render(reporter.Describe());
        }
        catch (Services.SparkSecurityConfigurationException ex)
        {
            // A configuration that does not compose has no posture: say why, as startup would.
            Console.Error.WriteLine("Spark: " + ex.Message);
            Environment.ExitCode = ExitMisconfigured;
            return true;
        }

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
        Console.WriteLine($"Spark: wrote the security posture baseline to {BaselineFile}.");
    }

    private static void Verify(string path, string current)
    {
        // A missing baseline is drift, not a pass. Treating it as "nothing to compare against" would
        // make deleting the file the way to silence the gate.
        var committed = File.Exists(path) ? File.ReadAllText(path) : null;

        if (string.Equals(NormalizeLineEndings(committed), NormalizeLineEndings(current), StringComparison.Ordinal))
            return;

        // Anonymous grants get their own warning (composition D4), so a widened public surface is
        // never just one more line in a long diff. The ::warning:: prefix is a GitHub annotation.
        if (!string.Equals(NormalizeLineEndings(AnonymousPart(committed)), NormalizeLineEndings(AnonymousPart(current)), StringComparison.Ordinal))
        {
            Console.WriteLine($"::warning title=Spark anonymous surface::{BaselineFile}: the rights reachable without signing in have changed.");
            Console.Error.WriteLine("Spark: the set of rights reachable without signing in has changed.");
        }
        else
        {
            Console.Error.WriteLine("Spark: the effective rights have changed.");
        }
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
    /// <para>
    /// The full effective table (composition D4), one row per right with the layer it came from.
    /// Grants to the anonymous group have their own section, with the expanded surface they reach,
    /// so they cannot hide in a long diff; a switched-off library's rights are listed as inert. Rows
    /// are separated by <c> | </c> rather than padded into columns, so a longer name added later does
    /// not rewrite every line.
    /// </para>
    /// </summary>
    internal static string Render(SecurityPosture posture)
    {
        var anonymous = (posture.Rights ?? []).Where(r => r.Anonymous).ToList();
        var builder = new StringBuilder();
        builder.Append("# Spark security posture: every effective right and the layer it comes from.\n");
        builder.Append($"# {SynchronizeFlag} writes this file; {VerifyFlag} fails when it drifts.\n");
        builder.Append("# Rows: group | effect | resource | key | layer\n");

        builder.Append('\n').Append(AnonymousHeader).Append('\n');
        AppendLines(builder, posture.AnonymouslyReachable);

        builder.Append("\n## Granted to @anonymous\n");
        AppendLines(builder, anonymous.Select(Line));

        builder.Append("\n## Rights\n");
        AppendLines(builder, (posture.Rights ?? []).Where(r => !r.Anonymous).Select(Line));

        builder.Append("\n## Inert: libraries switched off in \"libraries\"\n");
        AppendLines(builder, (posture.Inert ?? []).Select(Line));
        return builder.ToString();

        static string Line(SecurityPostureRow r) => $"{r.Group} | {r.Effect} | {r.Resource} | {r.Key} | {r.Layer}";
    }

    private const string AnonymousHeader = "## Reachable without signing in (expanded)";

    private static void AppendLines(StringBuilder builder, IEnumerable<string> lines)
    {
        var any = false;
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
            any = true;
        }
        if (!any) builder.Append("(nothing)\n");
    }

    /// <summary>The anonymous sections of a rendered posture, so their drift gets its own warning.</summary>
    private static string? AnonymousPart(string? rendered)
    {
        if (rendered is null) return null;
        var text = rendered.Replace("\r\n", "\n");
        var start = text.IndexOf(AnonymousHeader, StringComparison.Ordinal);
        var end = text.IndexOf("\n## Rights\n", StringComparison.Ordinal);
        return start < 0 || end < start ? text : text[start..end];
    }

    private static string? NormalizeLineEndings(string? value)
        => value?.Replace("\r\n", "\n").TrimEnd('\n');

    private static string Indent(string value)
        => string.Join("\n", value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => "  " + l));
}
