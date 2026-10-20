using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits <c>PersistentObjectNames</c>: one <c>const string</c> per persistent-object name the composed
/// model declares — the application's own model files and every referenced library's layer — so code
/// that names a type (<c>GetPersistentObjectAsync(PersistentObjectNames.Home)</c>,
/// <c>IsAllowedAsync(verb, PersistentObjectNames.Question)</c>) fails to compile when the type goes.
/// </summary>
/// <remarks>
/// The names come from the composed model, the same source as <c>PersistentObjectIds</c>, and never from
/// CLR class names: a persistent object need not have a class (a virtual <c>Home</c>), and its name is
/// what the model says it is (X20 item 1, D28 — the renames of M2 apply in the composition, so the
/// constants follow them without a change here).
/// </remarks>
public class PersistentObjectNamesProducer : Producer
{
    private readonly IEnumerable<string> names;
    private readonly bool knowsSpark;

    public PersistentObjectNamesProducer(
        IEnumerable<string> names,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "PersistentObjectNames.g.cs")
    {
        this.names = names;
        this.knowsSpark = knowsSpark;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        // The name is spliced into source, so only a C# identifier gets a constant (R2-C5).
        var list = names
            .Where(Json.ComposedModel.IsIdentifier)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (!knowsSpark || list.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("public static class PersistentObjectNames"))
            {
                foreach (var name in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine($"public const string {name} = \"{name}\";");
                }
            }
        }
    }
}
