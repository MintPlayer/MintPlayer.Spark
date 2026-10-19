using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;
using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The enum-backed lookup generator (commerce X20 item 5, G1): a <c>[SparkLookup]</c> enum becomes a
/// <c>TransientLookupReference</c> class whose labels are the <c>translations.json</c> keys
/// <c>lookup.{Enum}.{Member}</c>, never inline text.
/// </summary>
public class SparkLookupGeneratorTests
{
    private const string GeneratorName = "SparkLookupGenerator";
    private static readonly Type[] References = [typeof(TransientLookupReference<>)];

    private static GeneratorRunResult Run(string source)
        => GeneratorHarness.Run(GeneratorName, [source], referenceTypes: References, rootNamespace: "TestApp");

    private static string Generated(GeneratorRunResult result)
        => result.GeneratedSources.Should().ContainSingle().Which.Source;

    private static IEnumerable<Diagnostic> Errors(GeneratorRunResult result)
        => result.FinalCompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    [Fact]
    public void Enum_keyed_lookup_gets_one_keyed_item_per_member_in_declaration_order()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Lookups;

            [SparkLookup]
            public enum ECarStatus { Available, Sold, InMaintenance }
            """);

        var generated = Generated(result);
        generated.Should().Contain("namespace TestApp.Lookups");
        generated.Should().Contain("public sealed partial class CarStatus : global::MintPlayer.Spark.Abstractions.TransientLookupReference<global::TestApp.Lookups.ECarStatus>");
        generated.Should().Contain("ELookupDisplayType DisplayType => global::MintPlayer.Spark.Abstractions.ELookupDisplayType.Dropdown;");
        generated.Should().Contain("Values = global::MintPlayer.Spark.Abstractions.TranslatedString.FromKey(labelKey)");

        var available = generated.IndexOf("Create(global::TestApp.Lookups.ECarStatus.Available, \"lookup.ECarStatus.Available\"),", StringComparison.Ordinal);
        var sold = generated.IndexOf("Create(global::TestApp.Lookups.ECarStatus.Sold, \"lookup.ECarStatus.Sold\"),", StringComparison.Ordinal);
        var maintenance = generated.IndexOf("Create(global::TestApp.Lookups.ECarStatus.InMaintenance, \"lookup.ECarStatus.InMaintenance\"),", StringComparison.Ordinal);
        available.Should().BePositive();
        sold.Should().BeGreaterThan(available);
        maintenance.Should().BeGreaterThan(sold);

        result.GeneratorDiagnostics.Should().BeEmpty();
        Errors(result).Should().BeEmpty();
    }

    [Fact]
    public void String_keyed_lookup_keeps_stored_keys_and_emits_their_constants()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [SparkLookup(StringKeys = true, DisplayType = ELookupDisplayType.Modal)]
            public enum EProjectBasis
            {
                [SparkLookupKey("scoped")] Scoped,
                Projection,
            }
            """);

        var generated = Generated(result);
        generated.Should().Contain("public sealed partial class ProjectBasis : global::MintPlayer.Spark.Abstractions.TransientLookupReference<string>");
        generated.Should().Contain("public const string Scoped = \"scoped\";");
        generated.Should().Contain("public const string Projection = \"Projection\";");
        generated.Should().Contain("Create(\"scoped\", \"lookup.EProjectBasis.Scoped\"),");
        generated.Should().Contain("Create(\"Projection\", \"lookup.EProjectBasis.Projection\"),");
        generated.Should().Contain("ELookupDisplayType.Modal;");
        Errors(result).Should().BeEmpty();
    }

    /// <summary>
    /// The zero value is often the stored default, so the declaration order cannot always be the display
    /// order: [SparkLookupOrder] sorts the items, ties (and unmarked members, 0) in declaration order.
    /// </summary>
    [Fact]
    public void SparkLookupOrder_sorts_the_items_and_ties_keep_declaration_order()
    {
        var generated = Generated(Run("""
            using MintPlayer.Spark.Abstractions;
            namespace TestApp;
            [SparkLookup]
            public enum ECooldown
            {
                [SparkLookupOrder(3)] ThreeDays,
                None,
                [SparkLookupOrder(2)] OneDay,
                Zero,
            }
            """));

        var positions = new[] { "None", "Zero", "OneDay", "ThreeDays" }
            .Select(m => generated.IndexOf($"Create(global::TestApp.ECooldown.{m},", StringComparison.Ordinal))
            .ToList();
        positions.Should().OnlyContain(p => p > 0);
        positions.Should().Equal(positions.OrderBy(p => p).ToList());
    }

    [Fact]
    public void An_explicit_name_is_the_class_name_and_an_internal_enum_gives_an_internal_class()
    {
        var generated = Generated(Run("""
            using MintPlayer.Spark.Abstractions;
            namespace TestApp;
            [SparkLookup("OrganizationRoles")]
            internal enum OrganizationRole { Member, Admin }
            """));

        generated.Should().Contain("internal sealed partial class OrganizationRoles : global::MintPlayer.Spark.Abstractions.TransientLookupReference<global::TestApp.OrganizationRole>");
        generated.Should().Contain("\"lookup.OrganizationRole.Admin\"");
    }

    [Fact]
    public void Generated_items_carry_keys_and_label_keys_and_a_partial_Initialize_sets_extras()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [SparkLookup]
            public enum ECarStatus { Available, Sold }

            public sealed partial class CarStatus
            {
                public bool AllowOnCarNotes { get; private set; }

                static partial void Initialize(CarStatus item)
                    => item.AllowOnCarNotes = item.Key == ECarStatus.Sold;
            }
            """;
        var result = Run(source);
        Errors(result).Should().BeEmpty();

        var assembly = GeneratorHarness.EmitAndLoad(
            "SparkLookupFixture",
            [source, .. result.GeneratedSources.Select(s => s.Source)],
            References);

        var type = assembly.GetType("TestApp.CarStatus")!;
        var items = ((System.Collections.IEnumerable)type.GetProperty("Items")!.GetValue(null)!).Cast<TransientLookupReference>().ToList();

        items.Should().HaveCount(2);
        items.Select(i => i.Values.Key).Should().Equal("lookup.ECarStatus.Available", "lookup.ECarStatus.Sold");
        items.Should().OnlyContain(i => i.Values.IsEmpty, "a label is a key until the server resolves it");
        items.Select(i => type.GetProperty("Key")!.GetValue(i)!.ToString()).Should().Equal("Available", "Sold");
        items.Select(i => (bool)type.GetProperty("AllowOnCarNotes")!.GetValue(i)!).Should().Equal(false, true);
        items.Should().OnlyContain(i => i.DisplayType == ELookupDisplayType.Dropdown);
    }

    [Theory]
    [InlineData("[SparkLookup] public enum Status { A }", "is the enum's own name")]
    [InlineData("[SparkLookup] public enum EStatus { }", "has no members")]
    [InlineData("[SparkLookup] public enum EStatus { [SparkLookupKey(\"a\")] A }", "only a StringKeys lookup uses")]
    [InlineData("[SparkLookup] public enum EStatus { A = 1, B = 1 }", "share one value")]
    [InlineData("[SparkLookup(StringKeys = true)] public enum EStatus { [SparkLookupKey(\"x\")] A, [SparkLookupKey(\"x\")] B }", "share the key 'x'")]
    [InlineData("[SparkLookup(StringKeys = true)] public enum EStatus { Items }", "collide with a member of the generated class")]
    [InlineData("[SparkLookup(\"not valid\")] public enum EStatus { A }", "is not a valid C# class name")]
    [InlineData("public class Outer { [SparkLookup] public enum EStatus { A } }", "nested in a type")]
    public void A_lookup_that_cannot_be_emitted_is_reported_as_SPARK064(string declaration, string reason)
    {
        var result = Run($"""
            using MintPlayer.Spark.Abstractions;
            namespace TestApp;
            {declaration}
            """);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK064")
            .Which.GetMessage().Should().Contain(reason);
    }

    [Fact]
    public void Emits_nothing_without_the_Spark_model()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace TestApp; public enum EStatus { A }"],
            referenceTypes: Array.Empty<Type>(),
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_unmarked_enum_emits_nothing()
    {
        var result = Run("""
            namespace TestApp;
            public enum ECarStatus { Available, Sold }
            """);

        result.GeneratedSources.Should().BeEmpty();
    }
}
