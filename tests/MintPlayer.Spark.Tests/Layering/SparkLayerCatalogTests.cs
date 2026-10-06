using System.Reflection;
using System.Reflection.Emit;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// Composition M3 (D1 amended by S1, D2, D16): the run time finds the library layers the build
/// embedded, in layer order, through the assemblies the application recorded; two libraries with
/// one alias refuse to start.
/// </summary>
public class SparkLayerCatalogTests
{
    /// <summary>An in-memory assembly carrying the attributes a library's generator would emit.</summary>
    private static Assembly Library(string name, string alias, string[]? dependsOn = null, params (string Kind, string Path, string Json)[] layers)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
        var layer = typeof(SparkLayerAttribute).GetConstructor([typeof(string), typeof(string), typeof(string), typeof(string)])!;
        if (layers.Length == 0) layers = [("translations", "translations.json", "{}")];
        foreach (var (kind, path, json) in layers)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(layer, [alias, kind, path, json]));
        if (dependsOn is not null)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(SparkLayerDependenciesAttribute).GetConstructor([typeof(string[])])!, [dependsOn]));
        return assembly;
    }

    private static Assembly Recording(params string[] assemblyNames)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("App" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(SparkLayerAssembliesAttribute).GetConstructor([typeof(string[])])!, [assemblyNames]));
        return assembly;
    }

    private static Assembly Core => typeof(MintPlayer.Spark.Services.ActionsCatalogueLoader).Assembly;

    private static Assembly Authorization => Assembly.Load("MintPlayer.Spark.Authorization");

    [Fact]
    public void The_in_repo_libraries_ship_their_layers_under_their_aliases()
    {
        var libraries = SparkLayerCatalog.Discover([Authorization, Core]);

        libraries.Select(l => (l.AssemblyName, l.Alias)).Should().Equal(
            ("MintPlayer.Spark", "spark"),
            ("MintPlayer.Spark.Authorization", "authorization"));
        libraries[0].Layers.Select(l => (l.Kind, l.Path)).Should().Equal(("actions", "actions.json"), ("translations", "translations.json"));
        libraries[1].Layers.Select(l => (l.Kind, l.Path)).Should().Equal(("translations", "translations.json"));
    }

    [Fact]
    public void The_process_wide_catalogue_starts_with_the_core_library()
    {
        SparkLayerCatalog.Libraries.Should().NotBeEmpty();
        SparkLayerCatalog.Libraries[0].Alias.Should().Be("spark");
        SparkLayerCatalog.Of("actions").Should().Contain(x => x.Library.AssemblyName == "MintPlayer.Spark");
    }

    [Fact]
    public void Libraries_stack_above_their_dependencies_and_by_name_between_unrelated_ones()
    {
        var libraries = SparkLayerCatalog.Discover(
        [
            Library("Alpha.Ext", "alpha", ["Zeta.Lib"]),
            Library("Zeta.Lib", "zeta"),
            Library("Beta.Lib", "beta"),
            Core,
        ]);

        libraries.Select(l => l.AssemblyName).Should().Equal("MintPlayer.Spark", "Beta.Lib", "Zeta.Lib", "Alpha.Ext");
        libraries.Single(l => l.Alias == "alpha").DependsOn.Should().Equal("Zeta.Lib");
    }

    [Fact]
    public void Assemblies_without_layers_are_not_libraries()
    {
        SparkLayerCatalog.Discover([typeof(SparkLayerCatalogTests).Assembly, typeof(object).Assembly]).Should().BeEmpty();
    }

    [Fact]
    public void Two_libraries_with_one_alias_refuse_to_start()
    {
        var act = () => SparkLayerCatalog.Discover([Library("Acme.Auth", "auth"), Library("Other.Auth", "auth")]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Acme.Auth' and 'Other.Auth'*'auth'*SPARK043*");
    }

    [Fact]
    public void Layers_of_one_kind_come_in_layer_order_and_by_path_within_a_library()
    {
        var libraries = SparkLayerCatalog.Discover(
        [
            Library("B.Lib", "b", null, ("model", "Model/Zed.json", "{}"), ("model", "Model/Abe.json", "{}"), ("translations", "translations.json", "{}")),
            Library("A.Lib", "a", null, ("model", "Model/Mid.json", "{}")),
        ]);

        SparkLayerCatalog.Of(libraries, "model").Select(x => $"{x.Library.Alias}:{x.Layer.Path}")
            .Should().Equal("a:Model/Mid.json", "b:Model/Abe.json", "b:Model/Zed.json");
    }

    [Fact]
    public void The_assemblies_the_application_recorded_are_the_ones_loaded()
    {
        // S1: the compiler drops a reference whose types the application never uses, so the run time
        // loads the recorded names rather than trusting a reference walk.
        var loaded = SparkLayerCatalog.LayeredAssemblies(Recording("MintPlayer.Spark.Authorization", "MintPlayer.Spark")).ToList();

        loaded.Select(a => a.GetName().Name).Should().Equal("MintPlayer.Spark.Authorization", "MintPlayer.Spark");
    }

    [Fact]
    public void An_entry_assembly_that_recorded_nothing_falls_back_to_the_reference_walk()
    {
        // A WebApplicationFactory host: the entry assembly is the test runner, not the application.
        SparkLayerCatalog.LayeredAssemblies(entry: null).Should().Contain(Core);
        SparkLayerCatalog.LayeredAssemblies(typeof(object).Assembly).Should().Contain(Core);
    }

    [Fact]
    public void A_library_overriding_a_library_it_depends_on_is_not_a_conflict()
    {
        // Grill Q3: overriding a dependency is intended; only unrelated libraries conflict.
        var composition = SparkActionLayers.Compose(
        [
            new("Base.Lib", """{ "Archive": { "icon": "box" } }""", IsLibrary: true),
            new("Ext.Lib", """{ "Archive": { "icon": "archive" } }""", IsLibrary: true, DependsOn: ["Base.Lib"]),
            new("Other.Lib", """{ "Archive": { "icon": "other" } }""", IsLibrary: true),
        ]);

        composition.Conflicts.Should().ContainSingle().Which.Should().Be(
            new SparkActionsConflict("Archive", "icon", "Other.Lib", "Ext.Lib"));
    }
}
