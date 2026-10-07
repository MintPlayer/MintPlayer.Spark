using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// The action catalogue a test needs: the real library layers (core New/Edit/Delete) with an
/// application <c>actions.json</c> on top, composed and validated exactly as the loader does (#467, D7).
/// </summary>
internal static class TestActions
{
    /// <summary>The composed catalogue for <paramref name="appJson"/> (null: no application file).</summary>
    public static ActionsCatalogue Catalogue(string? appJson = null)
        => ActionsCatalogueLoader.Build(appJson, SparkActionLayers.Libraries);

    /// <summary>
    /// A catalogue declaring each of <paramref name="names"/> as a custom action shown on both sides
    /// with no selection rule, on top of the built-ins.
    /// </summary>
    public static ActionsCatalogue WithCustom(params string[] names)
        => Catalogue("{" + string.Join(",", names.Select(n => $"\"{n}\": {{ \"showedOn\": \"both\" }}")) + "}");

    /// <summary>A loader that always answers <paramref name="catalogue"/>.</summary>
    public static IActionsCatalogueLoader Loader(ActionsCatalogue catalogue) => new Fixed(catalogue);

    /// <summary>A loader for <see cref="WithCustom"/>.</summary>
    public static IActionsCatalogueLoader LoaderWithCustom(params string[] names) => Loader(WithCustom(names));

    private sealed class Fixed(ActionsCatalogue catalogue) : IActionsCatalogueLoader
    {
        public ActionsCatalogue GetCatalogue() => catalogue;
        public void Reload() { }
    }
}
