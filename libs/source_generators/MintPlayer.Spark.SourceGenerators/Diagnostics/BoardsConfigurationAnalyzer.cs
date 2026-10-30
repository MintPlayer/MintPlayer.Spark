using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Reports what is wrong in <c>App_Data/boards.json</c> at build time (docs/spark_ssr_PRD.md D14, D15).
/// </summary>
/// <remarks>
/// <para>
/// The host checks a board's structure, kinds, cap and paths at startup; this reports those a build earlier.
/// What the host never reports is a tile whose target names nothing: the tile kind's access check finds no
/// query, type or program unit and hides the tile as denied, so a typo reads as a permissions problem.
/// SPARK059 catches that, cross-referencing the composed model and the program units the way
/// <see cref="SecurityConfigurationAnalyzer"/> does for rights.
/// </para>
/// <para>
/// The runtime remains authoritative: a file this cannot parse gets no diagnostics, and a check without the
/// files it compares against (no model files, no program units) reports nothing.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BoardsConfigurationAnalyzer : DiagnosticAnalyzer
{
    /// <remarks>A warning: <c>SparkBoardsOptions.MaxTiles</c> can be raised in code or configuration, which a build cannot see.</remarks>
    internal static readonly DiagnosticDescriptor TileCapRule = new(
        id: "SPARK057",
        title: "Board declares more tiles than the default cap",
        messageFormat: "Board '{0}' declares {1} tiles; a board holds at most {2} (SparkBoardsOptions.MaxTiles), so the host refuses it at startup unless MaxTiles is raised",
        category: "Boards",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <remarks>An error: the host refuses the same file at startup.</remarks>
    internal static readonly DiagnosticDescriptor PathRule = new(
        id: "SPARK058",
        title: "Board path is invalid",
        messageFormat: "Board '{0}' path '{1}' {2}. The host refuses this boards.json at startup.",
        category: "Boards",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor UnknownTargetRule = new(
        id: "SPARK059",
        title: "Board tile names a target that does not exist",
        messageFormat: "Board '{0}' tile '{1}' {2}. The tile is hidden as if denied for every user — check the spelling.",
        category: "Boards",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary><c>SparkBoardsOptions.MaxTiles</c>'s default.</summary>
    internal const int DefaultMaxTiles = 20;

    /// <summary>The kinds <c>MintPlayer.Spark.Boards</c> registers itself (<c>SparkTileKinds</c>).</summary>
    private static readonly string[] BuiltInKinds = ["query", "count", "persistentObject", "programUnit"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TileCapRule, PathRule, UnknownTargetRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        var appDataDir = SparkAppDataDir.Read(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
        var file = context.Options.AdditionalFiles.FirstOrDefault(f =>
            IsNamed(f.Path, "boards.json") && SparkAppDataDir.Contains(f.Path, appDataDir));
        if (file?.GetText(context.CancellationToken) is not { } text)
            return;
        var content = text.ToString();
        if (ModelNamesReader.ParseOrNull(content) is not Dictionary<string, object?> root || Get(root, "boards") is not List<object?> boards)
            return;

        Lazy<Targets> targets = new(() => ReadTargets(context, appDataDir));
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var search = 0;

        foreach (var entry in boards)
        {
            if (entry is not Dictionary<string, object?> board || Get(board, "id") is not string boardId)
                continue;
            var boardLocation = Find(file, text, content, boardId, ref search);
            var tiles = (Get(board, "tiles") as List<object?> ?? []).OfType<Dictionary<string, object?>>().ToList();

            if (tiles.Count > DefaultMaxTiles)
                context.ReportDiagnostic(Diagnostic.Create(TileCapRule, boardLocation, boardId, tiles.Count, DefaultMaxTiles));

            foreach (var path in (Get(board, "paths") as List<object?> ?? []).OfType<string>())
            {
                var at = Find(file, text, content, path, ref search);
                if (!path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf('?') >= 0 || path.IndexOf('#') >= 0)
                    context.ReportDiagnostic(Diagnostic.Create(PathRule, at, boardId, path, "must be a page path starting with '/'"));
                else if (paths.TryGetValue(path, out var other) && other != boardId)
                    context.ReportDiagnostic(Diagnostic.Create(PathRule, at, boardId, path, $"is already the path of board '{other}'"));
                else
                    paths[path] = boardId;
            }

            foreach (var tile in tiles)
            {
                if (Get(tile, "id") is not string tileId || Get(tile, "kind") is not string kind)
                    continue;
                var at = Find(file, text, content, tileId, ref search);
                if (Problem(kind, Get(tile, "target") as Dictionary<string, object?>, targets.Value) is { } problem)
                    context.ReportDiagnostic(Diagnostic.Create(UnknownTargetRule, at, boardId, tileId, problem));
            }
        }
    }

    /// <summary>Why a built-in tile's target resolves to nothing, as the tile kind resolves it; null when it resolves or cannot be judged.</summary>
    private static string? Problem(string kind, Dictionary<string, object?>? target, Targets targets)
    {
        string? Value(string key) => target is null ? null : Get(target, key) as string;
        switch (kind)
        {
            // QueryLoader.ResolveQuery: a query id, else an alias (declared or derived), ignoring case.
            case "query":
            case "count":
                return Value("query") is { } query && targets.HasModel && !targets.Queries.Contains(query)
                    ? $"names the query '{query}', which is no query id or alias in the model"
                    : null;
            // ModelLoader.ResolveEntityType: an id, else an alias, else a name.
            case "persistentObject":
                return Value("entityType") is { } type && targets.HasModel && !targets.EntityTypes.Contains(type)
                    ? $"names the entity type '{type}', which is no persistent object id, alias or name in the model"
                    : null;
            case "programUnit":
                return Value("programUnit") is { } unit && targets.HasProgramUnits && !targets.ProgramUnits.Contains(unit)
                    ? $"names the program unit '{unit}', which programUnits.json does not declare"
                    : null;
            default:
                // An application kind is registered in code (AddKind), so only a compilation that declares no
                // ITileKind of its own can know the kind is unknown.
                return targets.DeclaresKinds || BuiltInKinds.Contains(kind)
                    ? null
                    : $"has kind '{kind}', which is not a built-in kind ({string.Join(", ", BuiltInKinds)}) and this application declares no ITileKind";
        }
    }

    private sealed class Targets
    {
        public bool HasModel { get; set; }
        public HashSet<string> Queries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> EntityTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasProgramUnits { get; set; }
        public HashSet<string> ProgramUnits { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DeclaresKinds { get; set; }
    }

    private static Targets ReadTargets(CompilationAnalysisContext context, string appDataDir)
    {
        var targets = new Targets();
        var files = context.Options.AdditionalFiles;

        // The composed model (composition D6): library types and queries count, as they do at runtime.
        var appFiles = new List<AppModelFileInfo>();
        foreach (var file in files)
        {
            if (!SparkAppDataDir.Contains(file.Path, appDataDir, "Model")) continue;
            if (file.GetText(context.CancellationToken) is not { } text) continue;
            appFiles.Add(new AppModelFileInfo { Path = file.Path, Text = text.ToString() });
        }
        targets.HasModel = appFiles.Count > 0;
        if (targets.HasModel)
        {
            foreach (var content in ComposedModel.Compose(ComposedModel.LibraryFiles(context.Compilation), appFiles))
            {
                if (ModelNamesReader.ParseOrNull(content) is not Dictionary<string, object?> model) continue;
                if (Get(model, "persistentObject") is Dictionary<string, object?> po)
                {
                    AddAll(targets.EntityTypes, Get(po, "id"), Get(po, "alias"), Get(po, "name"));
                    AddQueries(targets.Queries, Get(po, "queries"));
                }
                AddQueries(targets.Queries, Get(model, "queries"));
            }
        }

        var units = files.Where(f => IsNamed(f.Path, "programUnits.json") && SparkAppDataDir.Contains(f.Path, appDataDir))
            .Select(f => f.GetText(context.CancellationToken)?.ToString())
            .Concat(LibraryLayersReader.Read(context.Compilation)
                .SelectMany(l => l.Files.Where(f => f.Kind == SparkLayerKinds.ProgramUnits))
                .Select(f => f.Json))
            .OfType<string>();
        foreach (var json in units)
        {
            if (ModelNamesReader.ParseOrNull(json) is not Dictionary<string, object?> root) continue;
            targets.HasProgramUnits = true;
            foreach (var group in (Get(root, "programUnitGroups") as List<object?> ?? []).OfType<Dictionary<string, object?>>())
                foreach (var unit in (Get(group, "programUnits") as List<object?> ?? []).OfType<Dictionary<string, object?>>())
                    AddAll(targets.ProgramUnits, Get(unit, "id"));
        }

        targets.DeclaresKinds = DeclaresTileKind(context.Compilation.Assembly.GlobalNamespace);
        return targets;
    }

    private static void AddQueries(HashSet<string> into, object? queries)
    {
        foreach (var query in (queries as List<object?> ?? []).OfType<Dictionary<string, object?>>())
        {
            AddAll(into, Get(query, "id"), Get(query, "alias"));
            // SparkQueryAliases.Derive: a query without an alias is reachable at its name minus "Get", lowercased.
            if (Get(query, "alias") is not string { Length: > 0 } && Get(query, "name") is string name)
                into.Add(name.StartsWith("Get", StringComparison.OrdinalIgnoreCase) && name.Length > 3 ? name.Substring(3) : name);
        }
    }

    private static void AddAll(HashSet<string> into, params object?[] values)
    {
        foreach (var value in values)
            if (value is string { Length: > 0 } s)
                into.Add(s);
    }

    /// <summary>Whether this compilation declares a class implementing <c>MintPlayer.Spark.Boards.ITileKind</c>.</summary>
    private static bool DeclaresTileKind(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child && DeclaresTileKind(child))
                return true;
            if (member is INamedTypeSymbol type && type.AllInterfaces.Any(i => i.Name == "ITileKind" && i.ContainingNamespace.ToDisplayString() == "MintPlayer.Spark.Boards"))
                return true;
        }
        return false;
    }

    /// <summary>The next occurrence of <paramref name="value"/> as a JSON string, scanning forward so repeated values map to their own place.</summary>
    private static Location Find(AdditionalText file, SourceText text, string content, string value, ref int from)
    {
        var quoted = "\"" + value + "\"";
        var index = content.IndexOf(quoted, from, StringComparison.Ordinal);
        if (index < 0)
            index = content.IndexOf(quoted, StringComparison.Ordinal);
        if (index < 0)
            return Location.Create(file.Path, default, default);
        from = index + quoted.Length;
        var span = new TextSpan(index, quoted.Length);
        return Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span));
    }

    private static bool IsNamed(string path, string fileName)
        => path.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase);

    private static object? Get(Dictionary<string, object?> obj, string key)
    {
        foreach (var pair in obj)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }
}
