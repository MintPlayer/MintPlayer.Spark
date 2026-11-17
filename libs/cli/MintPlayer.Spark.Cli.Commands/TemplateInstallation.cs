namespace MintPlayer.Spark.Cli;

/// <summary>
/// Keeps the installed MintPlayer.Spark.Templates at the tool's own version (S3): the tool and the template ship from
/// the same commit, so <c>spark new</c> instantiates the template it was tested with.
/// </summary>
internal static class TemplateInstallation
{
    public const string PackageId = "MintPlayer.Spark.Templates";

    /// <summary>
    /// Reads the installed version from <c>dotnet new uninstall</c>'s listing, where each package's name line is
    /// followed by an indented <c>Version:</c> line. Null when the package is not installed.
    /// </summary>
    public static string? ParseInstalledVersion(string listing)
    {
        var lines = listing.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != PackageId) continue;
            var indent = Indent(lines[i]);
            for (var j = i + 1; j < lines.Length && (lines[j].Trim().Length == 0 || Indent(lines[j]) > indent); j++)
            {
                var line = lines[j].Trim();
                if (line.StartsWith("Version:", StringComparison.Ordinal)) return line["Version:".Length..].Trim();
            }
        }
        return null;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}
