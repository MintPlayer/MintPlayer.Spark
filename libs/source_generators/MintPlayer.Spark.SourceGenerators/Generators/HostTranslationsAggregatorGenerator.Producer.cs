using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;
using System.Linq;
using System.Text;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class HostTranslationsAggregatorProducer : Producer
{
    private readonly TranslationsAggregateInfo info;

    public HostTranslationsAggregatorProducer(TranslationsAggregateInfo info, string rootNamespace)
        : base(rootNamespace, "SparkTranslationsRegistry.g.cs")
    {
        this.info = info;
    }

    protected override void ProduceSource(IndentedTextWriter writer, System.Threading.CancellationToken cancellationToken)
    {
        if (!info.ShouldEmit) return;

        var merged = HostTranslationsAggregatorGenerator.MergeTranslations(info, out _);

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("using System.Collections.Generic;");
        writer.WriteLine("using MintPlayer.Spark.Abstractions;");
        writer.WriteLine();

        using (writer.OpenBlock("namespace MintPlayer.Spark.Generated"))
        {
            using (writer.OpenBlock("internal static class SparkTranslationsRegistry"))
            {
                writer.WriteLine("public static readonly IReadOnlyDictionary<string, TranslatedString> All = Build();");
                writer.WriteLine();

                writer.WriteLine("[System.Runtime.CompilerServices.ModuleInitializer]");
                using (writer.OpenBlock("internal static void Initialize()"))
                {
                    writer.WriteLine("SparkTranslations.Register(All);");
                }

                writer.WriteLine();
                using (writer.OpenBlock("private static IReadOnlyDictionary<string, TranslatedString> Build()"))
                {
                    writer.WriteLine("var d = new Dictionary<string, TranslatedString>(System.StringComparer.Ordinal);");
                    foreach (var kvp in merged.OrderBy(k => k.Key, System.StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"d[{Literal(kvp.Key)}] = {BuildTranslatedStringExpression(kvp.Value)};");
                    }
                    writer.WriteLine("return d;");
                }
            }
        }
    }

    private static string BuildTranslatedStringExpression(System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> langs)
    {
        var sb = new StringBuilder();
        sb.Append("new TranslatedString { Translations = new Dictionary<string, string> { ");
        for (var i = 0; i < langs.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('[').Append(Literal(langs[i].Key)).Append("] = ").Append(Literal(langs[i].Value));
        }
        sb.Append(" } }");
        return sb.ToString();
    }

    private static string Literal(string value)
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
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
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
