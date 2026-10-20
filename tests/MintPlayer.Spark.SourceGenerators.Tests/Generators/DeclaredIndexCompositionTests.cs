using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;
using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Warehouse WX16 / Q-W26 (accepted, Round 2), delivered by commerce X19: <c>Warehouse_Documents</c> maps document
/// types from the Inbound, Outbound and Packing packages, which the warehouse core cannot reference. The core ships
/// only the shared result type; each package declares its maps with <c>[SparkIndexMap]</c>; the application's
/// generator composes them into ONE multi-map index class. These fixtures mirror that assembly graph exactly:
/// core references no package, each package references core, the app references all four.
/// </summary>
public class DeclaredIndexCompositionTests
{
    private const string GeneratorName = "DeclaredIndexGenerator";
    private const string HintName = "SparkDeclaredIndexes.g.cs";

    private static readonly Type[] References =
    [
        typeof(SparkIndexMapAttribute),
        typeof(SparkMultiMapIndexCreationTask<>),
        typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask),
        typeof(System.Linq.Expressions.Expression),
    ];

    /// <summary>The warehouse core: the shared result type only. It references none of the packages.</summary>
    private const string CoreSource = """
        namespace Warehouse.Core;

        public class WarehouseDocumentEntry
        {
            public string Kind { get; set; } = string.Empty;
            public string State { get; set; } = string.Empty;
            public string? SiteId { get; set; }
            public string? OwnerId { get; set; }
            public System.DateTimeOffset? Date { get; set; }
        }
        """;

    private const string InboundSource = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using MintPlayer.Spark.Abstractions;
        using Warehouse.Core;

        namespace Warehouse.Inbound;

        public class Receipt
        {
            public string? Id { get; set; }
            public string Status { get; set; } = string.Empty;
            public string? SiteId { get; set; }
            public string? OwnerId { get; set; }
            public System.DateTimeOffset? ExpectedOn { get; set; }
        }

        public class CustomerReturn
        {
            public string? Id { get; set; }
            public string Status { get; set; } = string.Empty;
            public string? SiteId { get; set; }
        }

        public static class InboundIndexes
        {
            [SparkIndexMap("Warehouse_Documents")]
            public static Expression<System.Func<IEnumerable<Receipt>, IEnumerable<WarehouseDocumentEntry>>> Receipts { get; } =
                receipts => from receipt in receipts
                            select new WarehouseDocumentEntry
                            {
                                Kind = "receipt", State = receipt.Status, SiteId = receipt.SiteId,
                                OwnerId = receipt.OwnerId, Date = receipt.ExpectedOn,
                            };

            [SparkIndexMap("Warehouse_Documents")]
            public static Expression<System.Func<IEnumerable<CustomerReturn>, IEnumerable<WarehouseDocumentEntry>>> Returns { get; } =
                returns => from ret in returns
                           select new WarehouseDocumentEntry { Kind = "return", State = ret.Status, SiteId = ret.SiteId };
        }
        """;

    private const string OutboundSource = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using MintPlayer.Spark.Abstractions;
        using Warehouse.Core;

        namespace Warehouse.Outbound;

        public class OutboundOrder
        {
            public string? Id { get; set; }
            public string Status { get; set; } = string.Empty;
            public string? SiteId { get; set; }
            public string? OwnerId { get; set; }
        }

        public static class OutboundIndexes
        {
            [SparkIndexMap("Warehouse_Documents")]
            public static readonly Expression<System.Func<IEnumerable<OutboundOrder>, IEnumerable<WarehouseDocumentEntry>>> Orders =
                orders => from order in orders
                          select new WarehouseDocumentEntry { Kind = "outbound-order", State = order.Status, SiteId = order.SiteId, OwnerId = order.OwnerId };
        }
        """;

    private const string PackingSource = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using MintPlayer.Spark.Abstractions;
        using Warehouse.Core;

        namespace Warehouse.Packing;

        public class Shipment
        {
            public string? Id { get; set; }
            public string Status { get; set; } = string.Empty;
            public string? SiteId { get; set; }
            public System.DateTimeOffset? ShippedOn { get; set; }
        }

        public static class PackingIndexes
        {
            [SparkIndexMap("Warehouse_Documents")]
            public static Expression<System.Func<IEnumerable<Shipment>, IEnumerable<WarehouseDocumentEntry>>> Shipments { get; } =
                shipments => from shipment in shipments
                             select new WarehouseDocumentEntry { Kind = "shipment", State = shipment.Status, SiteId = shipment.SiteId, Date = shipment.ShippedOn };
        }
        """;

    /// <summary>
    /// A Packing package that brings its own result type: same index name, a field of the same name with another
    /// type (<c>State</c> is an <c>int</c> here). A multi-map index has one result type, so this cannot compose.
    /// </summary>
    private const string ConflictingPackingSource = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using MintPlayer.Spark.Abstractions;

        namespace Warehouse.Packing;

        public class Shipment
        {
            public string? Id { get; set; }
            public int Status { get; set; }
        }

        public class WarehouseDocumentEntry
        {
            public string Kind { get; set; } = string.Empty;
            public int State { get; set; }
        }

        public static class PackingIndexes
        {
            [SparkIndexMap("Warehouse_Documents")]
            public static Expression<System.Func<IEnumerable<Shipment>, IEnumerable<WarehouseDocumentEntry>>> Shipments { get; } =
                shipments => from shipment in shipments
                             select new WarehouseDocumentEntry { Kind = "shipment", State = shipment.Status };
        }
        """;

    private const string AppSource = "namespace WarehouseApp; public class Program { public static void Main() { } }";

    /// <summary>
    /// Compiles core, then each package against core only, then runs the generator on the application that
    /// references all of them. <paramref name="reversed"/> hands the packages to the app in the opposite order.
    /// </summary>
    private static GeneratorRunResult RunApp(string[] packageSources, string[] packageNames, bool reversed = false)
    {
        var core = GeneratorHarness.CompileToMetadataReference("Warehouse.Core", [CoreSource], References);
        var packages = packageSources
            .Select((source, i) => GeneratorHarness.CompileToMetadataReference(
                packageNames[i], [source], References, additionalReferences: [core]))
            .ToList();
        if (reversed) packages.Reverse();

        return GeneratorHarness.Run(
            GeneratorName,
            [AppSource],
            referenceTypes: References,
            rootNamespace: "WarehouseApp", outputKind: OutputKind.ConsoleApplication,
            additionalReferences: [core, .. packages]);
    }

    private static readonly string[] PackageNames = ["Warehouse.Inbound", "Warehouse.Outbound", "Warehouse.Packing"];

    private static IReadOnlyList<Diagnostic> ErrorsInGenerated(GeneratorRunResult result)
        => result.FinalCompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Location.SourceTree?.FilePath.EndsWith(HintName, StringComparison.Ordinal) == true)
            .ToList();

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void Three_packages_compose_one_Warehouse_Documents_index_in_the_application()
    {
        var result = RunApp([InboundSource, OutboundSource, PackingSource], PackageNames);

        var (hintName, generated) = result.GeneratedSources.Should().ContainSingle().Which;
        hintName.Should().EndWith(HintName);

        // One index class, not one per package.
        Occurrences(generated, "public partial class ").Should().Be(1);
        generated.Should().Contain("public partial class Warehouse_Documents : global::MintPlayer.Spark.SparkMultiMapIndexCreationTask<global::Warehouse.Core.WarehouseDocumentEntry>");

        // Every map of every package, two of them from one package.
        generated.Should().Contain("AddMap<global::Warehouse.Inbound.Receipt>(global::MintPlayer.Spark.SparkIndexMaps.Widen(global::Warehouse.Inbound.InboundIndexes.Receipts));");
        generated.Should().Contain("AddMap<global::Warehouse.Inbound.CustomerReturn>(global::MintPlayer.Spark.SparkIndexMaps.Widen(global::Warehouse.Inbound.InboundIndexes.Returns));");
        generated.Should().Contain("AddMap<global::Warehouse.Outbound.OutboundOrder>(global::MintPlayer.Spark.SparkIndexMaps.Widen(global::Warehouse.Outbound.OutboundIndexes.Orders));");
        generated.Should().Contain("AddMap<global::Warehouse.Packing.Shipment>(global::MintPlayer.Spark.SparkIndexMaps.Widen(global::Warehouse.Packing.PackingIndexes.Shipments));");
        Occurrences(generated, "AddMap<").Should().Be(4);

        // The field set is the core's result type: no [Search]/[Fuzzy] on it, no stored fields asked for.
        generated.Should().NotContain("FieldIndexing.Search");
        generated.Should().NotContain("StoreAllFields(");
        generated.Should().NotContain("SearchEngineType");

        result.GeneratorDiagnostics.Should().BeEmpty();
        ErrorsInGenerated(result).Should().BeEmpty("the composed index must compile against all three packages");
    }

    [Fact]
    public void The_composed_index_does_not_depend_on_the_order_of_the_package_references()
    {
        var forward = RunApp([InboundSource, OutboundSource, PackingSource], PackageNames).GeneratedSources.Single().Source;
        var backward = RunApp([InboundSource, OutboundSource, PackingSource], PackageNames, reversed: true).GeneratedSources.Single().Source;

        backward.Should().Be(forward);
        // Ordinal member order across and within packages ("Receipts" < "Returns").
        forward.IndexOf("InboundIndexes.Receipts", StringComparison.Ordinal)
            .Should().BeLessThan(forward.IndexOf("InboundIndexes.Returns", StringComparison.Ordinal));
        forward.IndexOf("InboundIndexes.Returns", StringComparison.Ordinal)
            .Should().BeLessThan(forward.IndexOf("OutboundIndexes.Orders", StringComparison.Ordinal));
        forward.IndexOf("OutboundIndexes.Orders", StringComparison.Ordinal)
            .Should().BeLessThan(forward.IndexOf("PackingIndexes.Shipments", StringComparison.Ordinal));
    }

    [Fact]
    public void A_package_whose_result_type_conflicts_is_reported_and_left_out()
    {
        var result = RunApp([InboundSource, OutboundSource, ConflictingPackingSource], PackageNames);

        // SPARK_INDEX_016 is an error, so the application build fails instead of deploying an index without
        // the Packing documents.
        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK_INDEX_016").Which;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        var message = diagnostic.GetMessage();
        message.Should().Contain("Warehouse_Documents");
        message.Should().Contain("Warehouse.Packing.PackingIndexes.Shipments");
        message.Should().Contain("global::Warehouse.Packing.WarehouseDocumentEntry");
        message.Should().Contain("global::Warehouse.Core.WarehouseDocumentEntry");

        // The maps that agree still compose; the conflicting one is skipped.
        var generated = result.GeneratedSources.Single().Source;
        generated.Should().Contain("SparkMultiMapIndexCreationTask<global::Warehouse.Core.WarehouseDocumentEntry>");
        Occurrences(generated, "AddMap<").Should().Be(3);
        generated.Should().NotContain("AddMap<global::Warehouse.Packing.Shipment>");
        ErrorsInGenerated(result).Should().BeEmpty();
    }

    [Fact]
    public void Field_options_of_one_contributor_apply_to_the_whole_composed_index()
    {
        // Outbound asks for stored fields; the others do not. The index stores them once for all maps.
        var storingOutbound = OutboundSource.Replace(
            "[SparkIndexMap(\"Warehouse_Documents\")]",
            "[SparkIndexMap(\"Warehouse_Documents\", StoreAllFields = true)]");

        var generated = RunApp([InboundSource, storingOutbound, PackingSource], PackageNames).GeneratedSources.Single().Source;

        Occurrences(generated, "StoreAllFields(global::Raven.Client.Documents.Indexes.FieldStorage.Yes);").Should().Be(1);
        Occurrences(generated, "AddMap<").Should().Be(4);
    }
}
