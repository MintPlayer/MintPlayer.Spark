using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

// Breadcrumb template in App_Data/Model/Profession.json: "{Description}".
public class Profession
{
    /// <summary>Unique identifier of the profession, assigned automatically on creation.</summary>
    public string? Id { get; set; }
    /// <summary>Name of the profession as shown to users, e.g. <c>Software engineer</c>.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Employment regime under which the profession is exercised, e.g. <c>Employee</c> or <c>Freelance</c>.</summary>
    public string Regime { get; set; } = string.Empty;
}
