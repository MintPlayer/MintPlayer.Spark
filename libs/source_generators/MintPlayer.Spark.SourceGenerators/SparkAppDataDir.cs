using Microsoft.CodeAnalysis.Diagnostics;
using System;

namespace MintPlayer.Spark.SourceGenerators;

/// <summary>
/// The application's Spark configuration directory as the generators see it: the
/// <c>SparkAppDataDir</c> MSBuild property (default <c>App_Data</c>), surfaced by
/// <c>&lt;CompilerVisibleProperty Include="SparkAppDataDir" /&gt;</c> in <c>spark.targets</c>.
/// The runtime twin is <c>MintPlayer.Spark.Abstractions.SparkAppData</c> (composition PRD D12).
/// </summary>
/// <remarks>
/// AdditionalFiles arrive with full paths and backslashes from a csproj glob, and with relative
/// paths and forward slashes from tests, so matching is by path segment, separator-agnostic and
/// ignoring case.
/// </remarks>
internal static class SparkAppDataDir
{
    internal const string BuildProperty = "build_property.SparkAppDataDir";
    internal const string Default = "App_Data";

    /// <summary>The configured directory, forward slashes, no trailing separator; <see cref="Default"/> when absent.</summary>
    public static string Read(AnalyzerConfigOptions globalOptions)
        => globalOptions.TryGetValue(BuildProperty, out var value) ? Normalize(value) : Default;

    internal static string Normalize(string? value)
    {
        if (value is null || value.Trim().Length == 0) return Default;
        var normalized = value.Trim().Replace('\\', '/').TrimEnd('/');

        // A relative "../Shared/Config" reaches the compiler as a full path without the dots, so only
        // the segments after them can be matched.
        while (normalized.StartsWith("./", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal))
            normalized = normalized.Substring(normalized.IndexOf('/') + 1);
        return normalized is "." or ".." ? string.Empty : normalized;
    }

    /// <summary>
    /// Whether <paramref name="filePath"/> lies under <paramref name="appDataDir"/>, or under its
    /// <paramref name="subdirectory"/> when one is given (e.g. <c>Model</c>).
    /// </summary>
    public static bool Contains(string filePath, string appDataDir, string? subdirectory = null)
    {
        var path = filePath.Replace('\\', '/');
        var needle = appDataDir;
        if (!string.IsNullOrEmpty(subdirectory))
            needle = needle.Length == 0 ? subdirectory! : needle + "/" + subdirectory;
        if (needle.Length == 0) return true;
        needle += "/";

        return path.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
            || path.IndexOf("/" + needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
