using CodeCoverage.Entities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeCoverage.Feedback;

/// <summary>
/// The optional in-repo policy file. It overrides the settings document
/// per field — only keys the file actually sets win — and it is read from the
/// <b>base ref</b>, never the head, so a pull request cannot rewrite the
/// policy it is judged by (roadmap §7.1). A malformed file changes nothing:
/// the stored settings stand and the parse error is surfaced on the feedback.
///
/// <code>
/// gate:
///   projectMode: fixed      # auto | fixed
///   projectTarget: 80
///   projectThreshold: 1
///   projectBasis: scoped    # scoped | projection
///   patchTarget: 80
///   patchThreshold: 5
///   blocking: true
/// </code>
/// </summary>
public static class CoverageYml
{
    public const string FileName = "coverage.yml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static GateSettings Merge(GateSettings stored, string? yml, out string? parseError)
    {
        parseError = null;
        if (string.IsNullOrWhiteSpace(yml))
            return stored;

        FileGate? overrides;
        try
        {
            overrides = Deserializer.Deserialize<FileRoot?>(yml)?.Gate;
        }
        catch (Exception ex)
        {
            parseError = $"coverage.yml ignored: {ex.Message}";
            return stored;
        }
        if (overrides is null)
            return stored;

        return new GateSettings
        {
            ProjectMode = Valid(overrides.ProjectMode, "auto", "fixed") ?? stored.ProjectMode,
            ProjectTarget = overrides.ProjectTarget ?? stored.ProjectTarget,
            ProjectThreshold = overrides.ProjectThreshold ?? stored.ProjectThreshold,
            ProjectBasis = Valid(overrides.ProjectBasis, "scoped", "projection") ?? stored.ProjectBasis,
            PatchTarget = overrides.PatchTarget ?? stored.PatchTarget,
            PatchThreshold = overrides.PatchThreshold ?? stored.PatchThreshold,
            Blocking = overrides.Blocking ?? stored.Blocking,
        };
    }

    private static string? Valid(string? value, params string[] allowed)
        => value is not null && allowed.Contains(value) ? value : null;

    private sealed class FileRoot
    {
        public FileGate? Gate { get; set; }
    }

    private sealed class FileGate
    {
        public string? ProjectMode { get; set; }
        public double? ProjectTarget { get; set; }
        public double? ProjectThreshold { get; set; }
        public string? ProjectBasis { get; set; }
        public double? PatchTarget { get; set; }
        public double? PatchThreshold { get; set; }
        public bool? Blocking { get; set; }
    }
}
