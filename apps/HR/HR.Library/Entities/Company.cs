using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

// The breadcrumb template lives in App_Data/Model/Company.json ("{Name} · {Sector}"); {Sector}
// is a reference to a Profession, so the Company breadcrumb embeds the Profession's breadcrumb —
// the middle link of the Person → Company → Profession chain.
public class Company
{
    /// <summary>Unique identifier of the company, assigned automatically on creation.</summary>
    public string? Id { get; set; }
    /// <summary>Legal or trading name of the company.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Public website of the company, including the scheme, e.g. <c>https://example.com</c>.</summary>
    public string? Website { get; set; }
    /// <summary>Approximate number of people the company employs.</summary>
    public int EmployeeCount { get; set; }
    /// <summary>Primary brand colour of the company as a hex code, e.g. <c>#0d6efd</c>.</summary>
    public string? BrandColor { get; set; }
    /// <summary>Secondary brand colour of the company as a hex code, used alongside the primary colour.</summary>
    public string? AccentColor { get; set; }

    /// <summary>The profession that best describes the sector the company operates in.</summary>
    [Reference(typeof(Profession))]
    public string? Sector { get; set; }
}
