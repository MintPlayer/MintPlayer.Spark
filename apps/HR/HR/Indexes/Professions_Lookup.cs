using HR.Entities;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace HR.Indexes;

/// <summary>
/// Profession's default index, written by hand beside the one <c>[GenerateIndex]</c> emits for it
/// (<c>Professions_Overview</c>). The explicit claim wins (<c>docs/default_index_PRD.md</c>, D5): the generator sees it
/// and emits its own index without the <c>[DefaultIndex]</c> marker, so the catalog elects this one at startup with no
/// conflict. The generated index stays deployed and queryable; it simply no longer shapes the model or serves lookups.
/// </summary>
/// <remarks>
/// A hand-written index earns its place by computing what the generator cannot: here the <see cref="VProfessionLookup.Label"/>
/// that pairs a profession with its regime, so the list tells "Nurse (Employee)" from "Nurse (Freelance)" and search
/// finds either word. On a single-map the type argument is optional (a bare <c>[DefaultIndex]</c> claims the mapped
/// type); it is spelled out here because naming the type is the point of the demo. Naming another type is SPARK051.
/// </remarks>
[DefaultIndex(typeof(Profession))]
public partial class Professions_Lookup : SparkIndexCreationTask<Profession>
{
    public Professions_Lookup()
    {
        Map = professions => from profession in professions
                             select new VProfessionLookup
                             {
                                 Id = profession.Id,
                                 Description = profession.Description,
                                 Regime = profession.Regime,
                                 Label = profession.Description + " (" + profession.Regime + ")",
                                 LabelSearch = profession.Description + " (" + profession.Regime + ")",
                             };

        StoreAllFields(FieldStorage.Yes);
    }
}

/// <summary>The view of <see cref="Professions_Lookup"/>, and so the shape of <c>Profession.json</c>'s list columns.</summary>
[FromIndex(typeof(Professions_Lookup))]
public partial class VProfessionLookup
{
    public string? Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Regime { get; set; } = string.Empty;
    [Search]
    public string Label { get; set; } = string.Empty;
}
