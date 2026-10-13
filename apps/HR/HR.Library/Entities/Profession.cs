using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

// Breadcrumb template in App_Data/Model/Profession.json: "{Description}".
//
// [GenerateIndex] still emits Professions_Overview and its VProfession view, but WITHOUT the
// [DefaultIndex] marker: the hand-written Professions_Lookup (HR/Indexes) claims Profession with
// [DefaultIndex(typeof(Profession))], and a hand-written claim wins (docs/default_index_PRD.md, D5;
// SPARK009 no longer calls this a clash). So Profession.json is shaped by VProfessionLookup and
// GetProfessions — the query Person.Professions' picker uses — runs on Professions_Lookup.
[GenerateIndex]
public class Profession
{
    /// <summary>Unique identifier of the profession, assigned automatically on creation.</summary>
    public string? Id { get; set; }
    /// <summary>Name of the profession as shown to users, e.g. <c>Software engineer</c>.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Employment regime under which the profession is exercised, e.g. <c>Employee</c> or <c>Freelance</c>.</summary>
    public string Regime { get; set; } = string.Empty;
}
