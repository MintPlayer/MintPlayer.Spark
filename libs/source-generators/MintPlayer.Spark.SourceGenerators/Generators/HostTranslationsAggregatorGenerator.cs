using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class HostTranslationsAggregatorGenerator : IncrementalGenerator
{
    private const string AttributeMetadataName = "MintPlayer.Spark.Abstractions.SparkTranslationsAttribute";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        // Referenced assemblies' translation attribute payloads, projected to POCOs immediately.
        var referencedProvider = context.CompilationProvider
            .Select(static (compilation, ct) =>
            {
                var isHost = compilation.Options.OutputKind == OutputKind.ConsoleApplication
                          || compilation.Options.OutputKind == OutputKind.WindowsApplication;
                if (!isHost) return new TranslationsAggregateInfo { ShouldEmit = false };

                var attrType = compilation.GetTypeByMetadataName(AttributeMetadataName);
                if (attrType is null) return new TranslationsAggregateInfo { ShouldEmit = false };

                var byAssembly = new Dictionary<string, TranslationsAssemblyInfo>();
                foreach (var asmSymbol in compilation.SourceModule.ReferencedAssemblySymbols)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var attr in asmSymbol.GetAttributes())
                    {
                        if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attrType))
                            continue;
                        if (attr.ConstructorArguments.Length != 3) continue;

                        var chunkIndex = attr.ConstructorArguments[0].Value as int? ?? 0;
                        var chunkCount = attr.ConstructorArguments[1].Value as int? ?? 0;
                        var json = attr.ConstructorArguments[2].Value as string ?? string.Empty;
                        var asmName = asmSymbol.Name;

                        if (!byAssembly.TryGetValue(asmName, out var info))
                        {
                            info = new TranslationsAssemblyInfo { AssemblyName = asmName };
                            byAssembly[asmName] = info;
                        }
                        info.Chunks.Add(new TranslationsChunkInfo
                        {
                            ChunkIndex = chunkIndex,
                            ChunkCount = chunkCount,
                            Json = json,
                        });
                    }
                }

                return new TranslationsAggregateInfo
                {
                    ShouldEmit = true,
                    Assemblies = byAssembly.Values.OrderBy(a => a.AssemblyName, System.StringComparer.Ordinal).ToList(),
                    OwnAssemblyName = compilation.AssemblyName ?? "",
                };
            })
            .WithComparer(ComparerRegistry.For<TranslationsAggregateInfo>());

        // Host's OWN translations.json, flattened (the aggregator can't see its own
        // compilation's generator-emitted attributes, so we re-flatten here).
        var ownProvider = context.AdditionalTextsProvider
            .Where(static t => string.Equals(Path.GetFileName(t.Path), "translations.json", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) =>
            {
                var text = t.GetText(ct)?.ToString();
                var info = new TranslationsAssemblyInfo();
                if (string.IsNullOrEmpty(text)) return info;
                JsonNode parsed;
                try { parsed = MiniJson.Parse(text!); }
                catch (JsonParseException) { return info; }

                var (entries, _) = TranslationsTreeFlattener.Flatten(parsed);
                if (entries.Count == 0) return info;

                info.Chunks.Add(new TranslationsChunkInfo
                {
                    ChunkIndex = 0,
                    ChunkCount = 1,
                    Json = MiniJson.Serialize(entries),
                });
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
            .WithComparer(ComparerRegistry.For<TranslationsAssemblyInfo>());

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

        // Diagnostics: conflicts between assemblies (host vs libraries or libraries vs libraries).
        context.RegisterSourceOutput(combined, static (spc, data) =>
        {
            if (!data.Aggregate.ShouldEmit) return;
            var merge = MergeTranslations(data.Aggregate, out var conflicts);
            foreach (var c in conflicts)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    TranslationsDiagnostics.ConflictingKey, Location.None,
                    c.Key, c.WinnerAssembly, c.LoserAssembly));
            }
        });

        var sourceProvider = combined.Select(static (data, ct) =>
            (Producer)new HostTranslationsAggregatorProducer(data.Aggregate, data.RootNamespace));

        context.ProduceCode(sourceProvider);
    }

    internal static Dictionary<string, List<KeyValuePair<string, string>>> MergeTranslations(
        TranslationsAggregateInfo info,
        out List<TranslationsConflict> conflicts)
    {
        // Host-wins merge: apply libraries (alphabetical) first, host last.
        // Tracks provenance for conflict reporting.
        var merged = new Dictionary<string, (string Owner, List<KeyValuePair<string, string>> Langs)>(System.StringComparer.Ordinal);
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
            result[kvp.Key] = kvp.Value.Langs;
        return result;
    }

    private static void ApplyAssembly(
        TranslationsAssemblyInfo asm,
        bool isHost,
        Dictionary<string, (string Owner, List<KeyValuePair<string, string>> Langs)> merged,
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
            var langs = new List<KeyValuePair<string, string>>();
            foreach (var lang in langObj.Members)
            {
                if (lang.Value is JsonString js)
                    langs.Add(new KeyValuePair<string, string>(lang.Key, js.Value));
            }

            if (merged.TryGetValue(key, out var existing))
            {
                // New value wins (last-write). Record the overwritten one as the conflict loser.
                conflicts.Add(new TranslationsConflict
                {
                    Key = key,
                    WinnerAssembly = asm.AssemblyName,
                    LoserAssembly = existing.Owner,
                });
            }
            merged[key] = (asm.AssemblyName, langs);
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
