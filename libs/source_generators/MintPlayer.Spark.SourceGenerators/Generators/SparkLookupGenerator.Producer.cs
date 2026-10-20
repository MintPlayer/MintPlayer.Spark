using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class SparkLookupProducer : Producer, IDiagnosticReporter
{
    private const string Abstractions = "global::MintPlayer.Spark.Abstractions";

    private readonly IReadOnlyList<SparkLookupInfo> lookups;
    private readonly bool knowsLookups;

    public SparkLookupProducer(IReadOnlyList<SparkLookupInfo> lookups, bool knowsLookups, string rootNamespace)
        : base(rootNamespace, "SparkLookups.g.cs")
    {
        this.lookups = lookups;
        this.knowsLookups = knowsLookups;
    }

    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        if (!knowsLookups) yield break;

        foreach (var invalid in lookups.Where(l => l.Error is not null).OrderBy(l => l.EnumFullName, System.StringComparer.Ordinal))
            yield return SparkLookupDiagnostics.InvalidLookup.Create(invalid.Location.ToLocation(compilation), invalid.EnumName, invalid.Error!);
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        if (!knowsLookups) return;

        var valid = lookups
            .Where(l => l.Error is null)
            .OrderBy(l => l.Namespace, System.StringComparer.Ordinal)
            .ThenBy(l => l.ClassName, System.StringComparer.Ordinal)
            .ToList();
        if (valid.Count == 0) return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");

        foreach (var group in valid.GroupBy(l => l.Namespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine();
            if (group.Key.Length == 0)
            {
                foreach (var lookup in group) WriteLookup(writer, lookup);
                continue;
            }

            using (writer.OpenBlock($"namespace {group.Key}"))
            {
                var first = true;
                foreach (var lookup in group)
                {
                    if (!first) writer.WriteLine();
                    first = false;
                    WriteLookup(writer, lookup);
                }
            }
        }
    }

    private static void WriteLookup(IndentedTextWriter writer, SparkLookupInfo lookup)
    {
        var keyType = lookup.StringKeys ? "string" : lookup.EnumFullName;

        writer.WriteLine("/// <summary>");
        writer.WriteLine($"/// The lookup of <see cref=\"{lookup.EnumFullName}\"/>, one item per member. Each label is the");
        writer.WriteLine($"/// <c>translations.json</c> key <c>lookup.{lookup.EnumName}.{{Member}}</c>.");
        writer.WriteLine("/// </summary>");
        using (writer.OpenBlock($"{lookup.Accessibility} sealed partial class {lookup.ClassName} : {Abstractions}.TransientLookupReference<{keyType}>"))
        {
            writer.WriteLine($"private {lookup.ClassName}() {{ }}");
            writer.WriteLine();

            if (lookup.StringKeys)
            {
                foreach (var member in lookup.Members)
                {
                    writer.WriteLine($"/// <summary>The stored key of <see cref=\"{lookup.EnumFullName}.{member.Name}\"/>.</summary>");
                    writer.WriteLine($"public const string {Identifier(member.Name)} = {Literal(member.StringKey)};");
                }
                writer.WriteLine();
            }

            writer.WriteLine($"public override {Abstractions}.ELookupDisplayType DisplayType => {Abstractions}.ELookupDisplayType.{lookup.DisplayType};");
            writer.WriteLine();

            writer.WriteLine($"/// <summary>One item per member of <see cref=\"{lookup.EnumFullName}\"/>, in declaration order.</summary>");
            writer.WriteLine($"public static global::System.Collections.Generic.IReadOnlyCollection<{lookup.ClassName}> Items {{ get; }} =");
            writer.WriteLine("[");
            writer.Indent++;
            foreach (var member in lookup.Members)
            {
                var key = lookup.StringKeys ? Literal(member.StringKey) : $"{lookup.EnumFullName}.{Identifier(member.Name)}";
                writer.WriteLine($"Create({key}, {Literal($"lookup.{lookup.EnumName}.{member.Name}")}),");
            }
            writer.Indent--;
            writer.WriteLine("];");
            writer.WriteLine();

            using (writer.OpenBlock($"private static {lookup.ClassName} Create({keyType} key, string labelKey)"))
            {
                writer.WriteLine($"var item = new {lookup.ClassName} {{ Key = key, Values = {Abstractions}.TranslatedString.FromKey(labelKey) }};");
                writer.WriteLine("Initialize(item);");
                writer.WriteLine("return item;");
            }
            writer.WriteLine();

            writer.WriteLine("/// <summary>");
            writer.WriteLine("/// Sets an item's extra properties. Implement it in a partial declaration of this class; it runs once per item.");
            writer.WriteLine("/// </summary>");
            writer.WriteLine($"static partial void Initialize({lookup.ClassName} item);");
        }
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}
