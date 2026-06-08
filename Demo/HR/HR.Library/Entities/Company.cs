using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

// {Sector} is a reference to a Profession, so the Company breadcrumb embeds the Profession's
// breadcrumb — the middle link of the Person → Company → Profession chain.
[Breadcrumb("{Name} · {Sector}")]
public class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int EmployeeCount { get; set; }
    public string? BrandColor { get; set; }
    public string? AccentColor { get; set; }

    [Reference(typeof(Profession))]
    public string? Sector { get; set; }
}
