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
public class LibraryTranslationsGenerator : IncrementalGenerator
{
    private const int MaxChunkBytes = 60 * 1024;

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        var translationsProvider = context.AdditionalTextsProvider
            .Where(static t => string.Equals(Path.GetFileName(t.Path), "translations.json", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) =>
            {
                var info = new TranslationsLibraryInfo { FilePath = t.Path };
                var text = t.GetText(ct)?.ToString();
                if (string.IsNullOrEmpty(text))
                {
                    info.Parsed = true;
                    return info;
                }

                JsonNode parsed;
                try
                {
                    parsed = MiniJson.Parse(text!);
                }
                catch (JsonParseException ex)
                {
                    info.Parsed = false;
                    info.ParseError = ex.Message;
                    return info;
                }

                var (entries, issues) = TranslationsTreeFlattener.Flatten(parsed);

                info.Parsed = true;
                info.Issues = issues
                    .Select(i => new TranslationsIssueInfo { Kind = i.Kind.ToString(), Path = i.Path })
                    .ToList();

                info.Chunks = ChunkSerialized(entries);
                return info;
            })
            .WithComparer(ComparerRegistry.For<TranslationsLibraryInfo>())
            .Collect();

        // Report diagnostics
        context.RegisterSourceOutput(translationsProvider, static (spc, infos) =>
        {
            foreach (var info in infos)
            {
                if (!info.Parsed)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        TranslationsDiagnostics.InvalidJson, Location.None, info.ParseError));
                    continue;
                }

                foreach (var issue in info.Issues)
                {
                    DiagnosticDescriptor descriptor = issue.Kind switch
                    {
                        nameof(TranslationsIssueKind.MixedLeafAndNamespace) => TranslationsDiagnostics.MixedLeafAndNamespace,
                        nameof(TranslationsIssueKind.EmptyObject) => TranslationsDiagnostics.EmptyObject,
                        nameof(TranslationsIssueKind.ArrayNotAllowed) => TranslationsDiagnostics.ArrayNotAllowed,
                        _ => TranslationsDiagnostics.InvalidJson,
                    };
                    spc.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, issue.Path));
                }
            }
        });

        // Emit [assembly: SparkTranslations(...)] attributes for each translations.json found.
        var sourceProvider = translationsProvider
            .Combine(settingsProvider)
            .Select(static (p, ct) =>
                (Producer)new LibraryTranslationsProducer(
                    p.Left,
                    p.Right.RootNamespace ?? "GeneratedCode"));

        context.ProduceCode(sourceProvider);
    }

    private static List<string> ChunkSerialized(
        List<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>> entries)
    {
        if (entries.Count == 0) return new List<string>();

        var chunks = new List<string>();
        var current = new List<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>>();
        var currentBytes = 2; // "{}"
        foreach (var entry in entries)
        {
            var oneEntryJson = MiniJson.Serialize(new[] { entry });
            var addedBytes = oneEntryJson.Length - 2 + (current.Count > 0 ? 1 : 0); // minus braces + optional comma
            if (current.Count > 0 && currentBytes + addedBytes > MaxChunkBytes)
            {
                chunks.Add(MiniJson.Serialize(current));
                current = new List<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>>();
                currentBytes = 2;
            }
            current.Add(entry);
            currentBytes += addedBytes;
        }
        if (current.Count > 0)
            chunks.Add(MiniJson.Serialize(current));
        return chunks;
    }
}
