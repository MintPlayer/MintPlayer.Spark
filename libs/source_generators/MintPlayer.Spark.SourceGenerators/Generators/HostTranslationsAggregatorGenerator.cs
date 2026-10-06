using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class HostTranslationsAggregatorGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        // Referenced libraries' translations layers ([assembly: SparkLayer], composition D1), in layer
        // order (core first, then by dependency), each flattened the way the host's own file is below.
        var referencedProvider = context.CompilationProvider
            .Select(static (compilation, ct) =>
            {
                var isHost = compilation.Options.OutputKind == OutputKind.ConsoleApplication
                          || compilation.Options.OutputKind == OutputKind.WindowsApplication;
                if (!isHost) return new TranslationsAggregateInfo { ShouldEmit = false };

                if (compilation.GetTypeByMetadataName(LibraryLayersReader.LayerAttribute) is null)
                    return new TranslationsAggregateInfo { ShouldEmit = false };

                var assemblies = new List<TranslationsAssemblyInfo>();
                foreach (var library in LibraryLayersReader.Read(compilation))
                {
                    ct.ThrowIfCancellationRequested();
                    var info = new TranslationsAssemblyInfo { AssemblyName = library.Assembly, DependsOn = library.DependsOn.ToList() };
                    foreach (var file in library.Files.Where(f => f.Kind == SparkLayerKinds.Translations))
                    {
                        if (Flatten(file.Json) is { } flat)
                            info.Chunks.Add(new TranslationsChunkInfo { ChunkIndex = 0, ChunkCount = 1, Json = flat });
                    }
                    if (info.Chunks.Count > 0) assemblies.Add(info);
                }

                return new TranslationsAggregateInfo
                {
                    ShouldEmit = true,
                    Assemblies = assemblies,
                    OwnAssemblyName = compilation.AssemblyName ?? "",
                };
            })
            ;

        // Host's OWN translations.json, flattened (the aggregator can't see its own
        // compilation's generator-emitted attributes, so we re-flatten here).
        var ownProvider = context.AdditionalTextsProvider
            .Where(static t => string.Equals(Path.GetFileName(t.Path), "translations.json", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) =>
            {
                var info = new TranslationsAssemblyInfo();
                if (Flatten(t.GetText(ct)?.ToString()) is { } flat)
                    info.Chunks.Add(new TranslationsChunkInfo { ChunkIndex = 0, ChunkCount = 1, Json = flat });
                return info;
            })
            .Collect()
            .Select(static (items, ct) =>
            {
                var all = new TranslationsAssemblyInfo();
                foreach (var item in items)
                    all.Chunks.AddRange(item.Chunks);
                return all;
            })
            ;

        var combined = referencedProvider
            .Combine(ownProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var aggregate = providers.Left.Left;
                var own = providers.Left.Right;
                var settings = providers.Right;

                aggregate.OwnAssembly = own.Chunks.Count > 0 ? own : null;
                return (Aggregate: aggregate, RootNamespace: settings.RootNamespace ?? "GeneratedCode");
            });

        // Diagnostics: two LIBRARIES giving the same (key, language) different values (#467, D3).
        // The host overriding a library is the intended way to customize, so it is never reported.
        context.RegisterSourceOutput(combined, static (spc, data) =>
        {
            if (!data.Aggregate.ShouldEmit) return;
            MergeTranslations(data.Aggregate, out var conflicts);
            foreach (var c in conflicts)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    TranslationsDiagnostics.ConflictingKey, Location.None,
                    c.Key, c.Language, c.WinnerAssembly, c.LoserAssembly));
            }
        });

        var sourceProvider = combined.Select(static (data, ct) =>
            (Producer)new HostTranslationsAggregatorProducer(data.Aggregate, data.RootNamespace));

        context.ProduceCode(sourceProvider);
    }

    /// <summary>A <c>translations.json</c> as one flat object of keys; <see langword="null"/> when it is empty or does not parse (SPARK_TRANS_001 says why).</summary>
    private static string? Flatten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        JsonNode parsed;
        try { parsed = MiniJson.Parse(text!); }
        catch (JsonParseException) { return null; }

        var (entries, _) = TranslationsTreeFlattener.Flatten(parsed);
        return entries.Count == 0 ? null : MiniJson.Serialize(entries);
    }

    internal static Dictionary<string, List<KeyValuePair<string, string>>> MergeTranslations(
        TranslationsAggregateInfo info,
        out List<TranslationsConflict> conflicts)
    {
        // Composition per (key, language) (#467, D2): libraries in layer order first (core, then by
        // dependency, alphabetical between unrelated ones; composition D2), host last. A later
        // layer replaces only the languages it defines, so an app adding `es` keeps the libraries' en/fr/nl
        // and an app overriding `nl` keeps the rest. Each language remembers the assembly that set it, for
        // conflict reporting.
        var merged = new Dictionary<string, List<(string Language, string Value, string Owner)>>(System.StringComparer.Ordinal);
        conflicts = new List<TranslationsConflict>();

        foreach (var asm in info.Assemblies)
            ApplyAssembly(asm, isHost: false, merged, conflicts);

        if (info.OwnAssembly is not null)
        {
            info.OwnAssembly.AssemblyName = string.IsNullOrEmpty(info.OwnAssemblyName) ? "<host>" : info.OwnAssemblyName;
            ApplyAssembly(info.OwnAssembly, isHost: true, merged, conflicts);
        }

        var result = new Dictionary<string, List<KeyValuePair<string, string>>>(System.StringComparer.Ordinal);
        foreach (var kvp in merged)
            result[kvp.Key] = kvp.Value.Select(l => new KeyValuePair<string, string>(l.Language, l.Value)).ToList();
        return result;
    }

    private static void ApplyAssembly(
        TranslationsAssemblyInfo asm,
        bool isHost,
        Dictionary<string, List<(string Language, string Value, string Owner)>> merged,
        List<TranslationsConflict> conflicts)
    {
        var reassembled = ReassembleChunks(asm);
        if (reassembled is null) return;

        JsonNode parsed;
        try { parsed = MiniJson.Parse(reassembled); }
        catch (JsonParseException) { return; }

        if (parsed is not JsonObject root) return;

        foreach (var entry in root.Members)
        {
            var key = entry.Key;
            if (entry.Value is not JsonObject langObj) continue;

            if (!merged.TryGetValue(key, out var langs))
            {
                langs = new List<(string Language, string Value, string Owner)>();
                merged[key] = langs;
            }

            foreach (var lang in langObj.Members)
            {
                if (lang.Value is not JsonString js) continue;
                // The app's "" means "not translated yet", never "blank it out" (#467, D23).
                if (isHost && js.Value.Length == 0) continue;

                var index = langs.FindIndex(l => string.Equals(l.Language, lang.Key, System.StringComparison.Ordinal));
                if (index < 0)
                {
                    // Appended, never inserted: TranslatedString.GetValue falls back to the FIRST language,
                    // so a layer adding a language must not change which one that is.
                    langs.Add((lang.Key, js.Value, asm.AssemblyName));
                    continue;
                }

                var existing = langs[index];
                if (!isHost && !asm.DependsOn.Contains(existing.Owner) && !string.Equals(existing.Value, js.Value, System.StringComparison.Ordinal))
                {
                    conflicts.Add(new TranslationsConflict
                    {
                        Key = key,
                        Language = lang.Key,
                        WinnerAssembly = asm.AssemblyName,
                        LoserAssembly = existing.Owner,
                    });
                }
                langs[index] = (lang.Key, js.Value, asm.AssemblyName);
            }

            if (langs.Count == 0) merged.Remove(key);
        }
    }

    private static string? ReassembleChunks(TranslationsAssemblyInfo asm)
    {
        if (asm.Chunks.Count == 0) return null;
        var expected = asm.Chunks[0].ChunkCount;
        if (expected <= 0) return null;
        var ordered = asm.Chunks.OrderBy(c => c.ChunkIndex).ToList();
        if (ordered.Count != expected) return null;

        // Every chunk is a standalone object — merge by concatenating members.
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        var first = true;
        for (var i = 0; i < ordered.Count; i++)
        {
            var payload = ordered[i].Json;
            if (string.IsNullOrEmpty(payload)) continue;
            var inner = payload.Trim();
            if (inner.Length < 2 || inner[0] != '{' || inner[inner.Length - 1] != '}')
                return null;
            inner = inner.Substring(1, inner.Length - 2).Trim();
            if (inner.Length == 0) continue;
            if (!first) sb.Append(',');
            sb.Append(inner);
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }
}
