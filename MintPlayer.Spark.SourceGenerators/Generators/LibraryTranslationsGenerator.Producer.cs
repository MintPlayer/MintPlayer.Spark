using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Text;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class LibraryTranslationsProducer : Producer
{
    private readonly System.Collections.Immutable.ImmutableArray<TranslationsLibraryInfo> infos;

    public LibraryTranslationsProducer(System.Collections.Immutable.ImmutableArray<TranslationsLibraryInfo> infos, string rootNamespace)
        : base(rootNamespace, "SparkTranslationsAssemblyAttributes.g.cs")
    {
        this.infos = infos;
    }

    protected override void ProduceSource(IndentedTextWriter writer, System.Threading.CancellationToken cancellationToken)
    {
        // Combine all translations.json files into a single flat entry set,
        // then re-chunk so we stay under the attribute-arg size ceiling across all sources.
        var combinedChunks = new List<string>();
        foreach (var info in infos)
        {
            if (!info.Parsed) continue;
            combinedChunks.AddRange(info.Chunks);
        }

        if (combinedChunks.Count == 0) return;

        writer.WriteLine(Header);
        writer.WriteLine();

        for (var i = 0; i < combinedChunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var literal = BuildCSharpStringLiteral(combinedChunks[i]);
            writer.WriteLine($"[assembly: global::MintPlayer.Spark.Abstractions.SparkTranslationsAttribute({i}, {combinedChunks.Count}, {literal})]");
        }
    }

    /// <summary>
    /// Builds a regular C# string literal "..." from an arbitrary UTF-16 string,
    /// escaping for inclusion in source code. Verbatim/raw strings would be
    /// more readable but complicate escaping of quotes inside translation templates.
    /// </summary>
    private static string BuildCSharpStringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\0': sb.Append("\\0"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                default:
                    if (c < 0x20 || c == 0x7f)
                        sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
