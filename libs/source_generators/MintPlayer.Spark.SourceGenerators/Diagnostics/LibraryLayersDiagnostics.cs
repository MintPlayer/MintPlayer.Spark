using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// The library alias (composition D16): required, well-formed, specific, and unique among an
/// application's libraries. It is stamped into right keys, slot tokens and model ids, so it is
/// effectively permanent once an application depends on it.
/// </summary>
internal static class LibraryLayersDiagnostics
{
    internal const string AliasProperty = "build_property.SparkLibraryAlias";

    public static readonly DiagnosticDescriptor AliasRequired = new(
        id: "SPARK041",
        title: "A library that ships layers needs a valid SparkLibraryAlias",
        messageFormat: "This library ships {0} App_Data layer file(s) but its SparkLibraryAlias {1}. Set <SparkLibraryAlias>…</SparkLibraryAlias> in the csproj: lower case letters, digits and single dashes, starting with a letter (e.g. 'authorization'). Choose it like a package id; it is part of every right key and model id the library ships.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AliasTooGeneric = new(
        id: "SPARK042",
        title: "The SparkLibraryAlias is too generic",
        messageFormat: "SparkLibraryAlias '{0}' is too generic to stay unique among the libraries an application references. Choose a name that identifies this library, like its package id would.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateAlias = new(
        id: "SPARK043",
        title: "Two referenced libraries declare the same SparkLibraryAlias",
        messageFormat: "Libraries '{1}' and '{2}' both declare the SparkLibraryAlias '{0}'. An alias names a library's rights and ids, so it must be unique; the application cannot start with both.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ModelIdNotDerived = new(
        id: "SPARK045",
        title: "A library model id is not the one derived from its alias and name",
        messageFormat: "{0}: '{1}' must be \"{2}\" (UUIDv5 of '{3}'), but it is {4}. A library's model ids are derived, never minted, so every application sees the same id (composition D5). Run 'npm run stamp:library-model-ids -- <library project folder>' or write the value by hand.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ModelLayerUnreadable = new(
        id: "SPARK046",
        title: "A library model file cannot be composed",
        messageFormat: "{0} cannot be shipped as a model layer: {1}. Applications would refuse to start with it.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly Regex AliasPattern = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Aliases that say nothing about the library. A small list on purpose: it catches the names a
    /// template or a first attempt would pick, not every poor choice.
    /// </summary>
    internal static readonly HashSet<string> GenericAliases = new(System.StringComparer.Ordinal)
    {
        "app", "application", "base", "common", "core", "default", "extensions", "lib", "library",
        "main", "module", "plugin", "shared", "test", "tests", "util", "utils",
    };

    /// <summary>Why <paramref name="alias"/> cannot be used, or <see langword="null"/> when it can.</summary>
    public static string? AliasProblem(string? alias)
    {
        if (alias is null || alias.Trim().Length == 0) return "is not set";
        return AliasPattern.IsMatch(alias) ? null : $"'{alias}' is not well-formed";
    }
}
