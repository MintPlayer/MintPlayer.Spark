using CodeCoverage.Git.Backports;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The <c>backport:</c> section of <c>coverage.yml</c> (#570, PRD §5.8), read from the DEFAULT branch like
/// <c>mergeQueue:</c> (O16): a line never governs how changes reach it. Absent means the defaults. Errors are returned,
/// never thrown; a section with errors is reported on the backport and the defaults apply.
/// <code>
/// backport:
///   onConflict: comment          # comment (default, D7) | draft: push the conflict markers, open a draft pull request
///   markers:                     # D8
///     disable: [setup-action]    # tfm | framework-packages | global.json | setup-action | npm-frameworks
///     packages: ["@mintplayer/ng-bootstrap", "MintPlayer.Spark*"]
/// </code>
/// </summary>
public static class BackportYml
{
    public const string Comment = "comment";
    public const string Draft = "draft";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static BackportYmlResult Parse(string? yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            return new(BackportSection.Default, []);

        FileBackport? file;
        try
        {
            file = Deserializer.Deserialize<FileRoot?>(yml)?.Backport;
        }
        catch (Exception ex)
        {
            return new(BackportSection.Default, [$"coverage.yml could not be read: {ex.Message}"]);
        }
        if (file is null)
            return new(BackportSection.Default, []);

        var errors = new List<string>();
        var onConflict = file.OnConflict?.Trim() ?? Comment;
        if (onConflict is not (Comment or Draft))
        {
            errors.Add($"backport.onConflict '{onConflict}' is not one of: {Comment}, {Draft}.");
            onConflict = Comment;
        }

        var disable = (file.Markers?.Disable ?? []).Select(k => k?.Trim() ?? "").ToList();
        foreach (var kind in disable.Where(k => !PlatformMarkerOptions.KindNames.Contains(k)))
            errors.Add($"backport.markers.disable '{kind}' is not a marker kind: {string.Join(", ", PlatformMarkerOptions.KindNames)}.");
        var packages = (file.Markers?.Packages ?? []).Select(p => p?.Trim() ?? "").ToList();
        foreach (var package in packages.Where(p => !PlatformMarkerOptions.PackagePattern.IsMatch(p)))
            errors.Add($"backport.markers.packages '{package}' is not a package name or pattern (letters, digits, @ / . _ - and *).");

        if (errors.Count > 0)
            return new(BackportSection.Default with { OnConflict = onConflict }, errors);
        return new(new BackportSection(onConflict,
            new PlatformMarkerOptions([.. disable.Distinct(StringComparer.OrdinalIgnoreCase)], [.. packages.Distinct(StringComparer.OrdinalIgnoreCase)])), []);
    }

    private sealed class FileRoot
    {
        public FileBackport? Backport { get; set; }
    }

    private sealed class FileBackport
    {
        public string? OnConflict { get; set; }
        public FileMarkers? Markers { get; set; }
    }

    private sealed class FileMarkers
    {
        public List<string?>? Disable { get; set; }
        public List<string?>? Packages { get; set; }
    }
}

/// <param name="OnConflict"><see cref="BackportYml.Comment"/> or <see cref="BackportYml.Draft"/>.</param>
public sealed record BackportSection(string OnConflict, PlatformMarkerOptions Markers)
{
    public static readonly BackportSection Default = new(BackportYml.Comment, new PlatformMarkerOptions([], []));
}

public sealed record BackportYmlResult(BackportSection Section, IReadOnlyList<string> Errors);
