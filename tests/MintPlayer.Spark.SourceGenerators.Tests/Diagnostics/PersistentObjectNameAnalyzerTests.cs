using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// D28 / X9, spike S11: SPARK057–062, persistent-object name identity checked at build time with the run
/// time's own composer and rename planner. A metadata reference carrying <c>[assembly: SparkLayer]</c>
/// stands in for a library. An intentional delta (<c>$extends</c>) and an application exposing a library's
/// own CLR type (SeoTester's <c>Organization</c>) stay silent.
/// </summary>
public class PersistentObjectNameAnalyzerTests
{
    private const string AnalyzerName = "PersistentObjectNameAnalyzer";
    private const string CodeFixName = "PersistentObjectNameCodeFixProvider";

    private static readonly Type[] References =
    [
        typeof(SparkLayerAttribute),
        typeof(PersistentObjectNameAttribute),
        typeof(SparkContext),
        typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask),
    ];

    private static string Layer(string alias, string path, string json)
        => $"[assembly: MintPlayer.Spark.Abstractions.SparkLayer({SymbolDisplay.FormatLiteral(alias, quote: true)}, \"model\", {SymbolDisplay.FormatLiteral(path, quote: true)}, {SymbolDisplay.FormatLiteral(json, quote: true)})]";

    private static string Order(string id, string clrType) => $$"""
        { "persistentObject": { "id": "{{id}}", "name": "Order", "clrType": "{{clrType}}", "attributes": [] } }
        """;

    /// <summary>The commerce library: the type <c>Order</c>, mapped to its own class <c>Commerce.Order</c>.</summary>
    private static MetadataReference Commerce()
        => GeneratorHarness.CompileToMetadataReference(
            "Commerce.Lib",
            [
                Layer("commerce", "Model/Order.json", Order("6a1e1c38-0000-5000-8000-000000000001", "Commerce.Order")),
                "namespace Commerce { public class Order { public string? Id { get; set; } } }",
            ],
            [typeof(SparkLayerAttribute)]);

    /// <summary>An unrelated library also shipping <c>Order</c>.</summary>
    private static MetadataReference Ticketing()
        => GeneratorHarness.CompileToMetadataReference(
            "Ticketing.Lib",
            [Layer("ticketing", "Model/Order.json", Order("6a1e1c38-0000-5000-8000-000000000002", "Ticketing.Order"))],
            [typeof(SparkLayerAttribute)]);

    private static string Context(string roots, string usings = "") => $$"""
        using MintPlayer.Spark;
        using Raven.Client.Documents.Linq;
        {{usings}}

        namespace MyApp;

        public class Order { public string? Id { get; set; } }

        public class AppContext : SparkContext
        {
            {{roots}}
        }
        """;

    private static Task<IReadOnlyList<Diagnostic>> RunAsync(
        IEnumerable<string> sources,
        MetadataReference[] libraries,
        IEnumerable<(string Path, string Text)>? appFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName, sources, referenceTypes: References, additionalTexts: appFiles,
            additionalReferences: libraries, globalOptions: globalOptions);

    // ── SPARK057 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_libraries_shipping_one_name_is_SPARK057_on_the_context()
    {
        var diagnostics = await RunAsync([Context("")], [Commerce(), Ticketing()]);

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "SPARK057").Which;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.Location.IsInSource.Should().BeTrue("anchored on the SparkContext, so the IDE offers the fix");
        diagnostic.GetMessage().Should().Contain("'Commerce.Lib' and 'Ticketing.Lib'").And.Contain("PersistentObjectName(\"ticketing\", \"Order\"");
    }

    [Fact]
    public async Task Without_a_context_SPARK057_is_reported_without_a_location()
    {
        var diagnostics = await RunAsync(["public class Placeholder { }"], [Commerce(), Ticketing()]);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK057").Which.Location.Should().Be(Location.None);
    }

    [Fact]
    public async Task Renaming_one_of_them_resolves_SPARK057()
    {
        var diagnostics = await RunAsync(
            [Context(""), """[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("ticketing", "Order", "TicketOrder")]"""],
            [Commerce(), Ticketing()]);

        diagnostics.Should().NotContain(d => d.Id == "SPARK057" || d.Id == "SPARK061");
    }

    [Fact]
    public async Task The_SPARK057_fix_states_the_rename()
    {
        var result = await CodeFixHarness.RunAnalyzerFixAsync(
            AnalyzerName, CodeFixName, "SPARK057",
            [
                FixtureProject.Of("Commerce.Lib", ("Layers.cs", Layer("commerce", "Model/Order.json", Order("6a1e1c38-0000-5000-8000-000000000001", "Commerce.Order")))),
                FixtureProject.Of("Ticketing.Lib", ("Layers.cs", Layer("ticketing", "Model/Order.json", Order("6a1e1c38-0000-5000-8000-000000000002", "Ticketing.Order")))),
                FixtureProject.Of("MyApp", ("AppContext.cs", Context(""))),
            ],
            referenceTypes: References);

        result.Diagnostics.Should().NotBeEmpty();
        result.Document("AppContext.cs").Should()
            .Contain("[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName(\"ticketing\", \"Order\", \"TicketingOrder\")]");
    }

    // ── SPARK058 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_application_class_taking_a_library_name_is_SPARK058_on_the_root()
    {
        var diagnostics = await RunAsync([Context("public IRavenQueryable<Order> Orders => Session.Query<Order>();")], [Commerce()]);

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "SPARK058").Which;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("'MyApp.Order' is named 'Order'").And.Contain("commerce:Order");
    }

    [Fact]
    public async Task Exposing_the_library_s_own_class_is_silent()
    {
        var diagnostics = await RunAsync(
            [Context("public IRavenQueryable<Commerce.Order> Orders => Session.Query<Commerce.Order>();")],
            [Commerce()]);

        diagnostics.Should().BeEmpty("SeoTester exposes the Organizations library's own Organization");
    }

    [Fact]
    public async Task Naming_the_application_class_apart_resolves_SPARK058_but_the_shared_collection_stays_SPARK062()
    {
        var diagnostics = await RunAsync(
            [
                Context("public IRavenQueryable<Order> Orders => Session.Query<Order>();"),
                """[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName<MyApp.Order>("ShopOrder")]""",
            ],
            [Commerce()]);

        diagnostics.Should().NotContain(d => d.Id == "SPARK058");
        var shared = diagnostics.Should().ContainSingle(d => d.Id == "SPARK062").Which;
        shared.Severity.Should().Be(DiagnosticSeverity.Warning);
        shared.GetMessage().Should().Contain("'MyApp.Order' and 'Commerce.Order'");
    }

    [Fact]
    public async Task The_SPARK058_fix_names_the_application_class()
    {
        var result = await CodeFixHarness.RunAnalyzerFixAsync(
            AnalyzerName, CodeFixName, "SPARK058",
            [
                FixtureProject.Of("Commerce.Lib", ("Layers.cs", Layer("commerce", "Model/Order.json", Order("6a1e1c38-0000-5000-8000-000000000001", "Commerce.Order")))),
                FixtureProject.Of("MyApp", ("AppContext.cs", Context("public IRavenQueryable<Order> Orders => Session.Query<Order>();"))),
            ],
            referenceTypes: References);

        result.Document("AppContext.cs").Should()
            .Contain("[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName<global::MyApp.Order>(\"MyAppOrder\")]");
    }

    // ── SPARK059 / SPARK060 ────────────────────────────────────────────────────────────────────

    private const string AppOrderPath = "C:\\app\\App_Data\\Model\\Order.json";

    [Fact]
    public async Task A_model_file_naming_a_library_type_without_extends_is_SPARK059()
    {
        var diagnostics = await RunAsync(
            ["public class Placeholder { }"], [Commerce()],
            [(AppOrderPath, """{ "persistentObject": { "name": "Order", "attributes": [] } }""")]);

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "SPARK059").Which;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning, "the file can lag the C# mid-build; an error would block synchronize (S11)");
        diagnostic.GetMessage().Should().Contain("\"$extends\": \"commerce:Order\"");
    }

    [Fact]
    public async Task An_extends_delta_is_silent()
    {
        var diagnostics = await RunAsync(
            ["public class Placeholder { }"], [Commerce()],
            [(AppOrderPath, """{ "$extends": "commerce:Order", "persistentObject": { "name": "Order", "breadcrumb": "{Id}" } }""")]);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_orphaned_delta_is_SPARK060()
    {
        var diagnostics = await RunAsync(
            ["public class Placeholder { }"], [Commerce()],
            [(AppOrderPath, """{ "$extends": "commerce:Basket", "persistentObject": { "name": "Basket" } }""")]);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK060").Which.GetMessage().Should().Contain("orphaned");
    }

    [Fact]
    public async Task A_delta_under_a_name_the_type_no_longer_has_is_SPARK060()
    {
        var diagnostics = await RunAsync(
            ["""[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("commerce", "Order", "ShopOrder")]"""], [Commerce()],
            [(AppOrderPath, """{ "$extends": "commerce:Order", "persistentObject": { "name": "Order" } }""")]);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK060").Which.GetMessage().Should().Contain("stale delta").And.Contain("'ShopOrder'");
    }

    // ── SPARK061 ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("nosuch", "Order", "X")]""", "no referenced library has the alias 'nosuch'")]
    [InlineData("""[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("commerce", "Basket", "X")]""", "ships no persistent-object type named 'Basket'")]
    [InlineData("""[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("commerce", "Order", "Shop.Order")]""", "not a valid persistent-object name")]
    public async Task An_invalid_rename_is_SPARK061_on_the_attribute(string attribute, string expected)
    {
        var diagnostics = await RunAsync([attribute], [Commerce()]);

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "SPARK061").Which;
        diagnostic.Location.IsInSource.Should().BeTrue();
        diagnostic.GetMessage().Should().Contain(expected);
    }

    [Fact]
    public async Task Renaming_one_type_twice_is_SPARK061()
    {
        var diagnostics = await RunAsync(
            [
                """[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("commerce", "Order", "ShopOrder")]""",
                """[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName<Commerce.Order>("WebOrder")]""",
            ],
            [Commerce()]);

        diagnostics.Should().Contain(d => d.Id == "SPARK061" && d.GetMessage().Contains("renamed more than once"));
    }

    [Fact]
    public async Task A_rename_onto_a_name_another_type_has_is_SPARK061()
    {
        var diagnostics = await RunAsync(
            [
                Context(""),
                """[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("ticketing", "Order", "order")]""",
            ],
            [Commerce(), Ticketing()]);

        diagnostics.Should().Contain(d => d.Id == "SPARK061" && d.GetMessage().Contains("ignoring case"));
    }

    [Fact]
    public async Task A_library_may_not_rename()
    {
        var diagnostics = await RunAsync(
            ["""[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("commerce", "Order", "ShopOrder")]"""],
            [Commerce()],
            globalOptions: new Dictionary<string, string> { ["build_property.SparkLibraryAlias"] = "shop" });

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK061").Which.GetMessage().Should().Contain("only an application may rename");
    }

    // ── SPARK062 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_roots_with_one_simple_name_share_a_collection_SPARK062()
    {
        var diagnostics = await RunAsync(
            ["""
             using MintPlayer.Spark;
             using Raven.Client.Documents.Linq;

             namespace MyApp.Sales { public class Item { public string? Id { get; set; } } }
             namespace MyApp.Stock { public class Item { public string? Id { get; set; } } }

             namespace MyApp
             {
                 public class AppContext : SparkContext
                 {
                     public IRavenQueryable<MyApp.Sales.Item> SalesItems => Session.Query<MyApp.Sales.Item>();
                     public IRavenQueryable<MyApp.Stock.Item> StockItems => Session.Query<MyApp.Stock.Item>();
                 }
             }
             """],
            [Commerce()]);

        diagnostics.Where(d => d.Id == "SPARK062").Should().HaveCount(2, "each root is told about the other");
    }
}
