namespace HR.Entities;

public class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int EmployeeCount { get; set; }
    public string? BrandColor { get; set; }
    public string? AccentColor { get; set; }
}
