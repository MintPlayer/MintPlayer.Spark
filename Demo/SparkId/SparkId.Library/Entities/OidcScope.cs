namespace SparkId.Entities;

public class OidcScope
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public List<string> ClaimTypes { get; set; } = [];
    public bool Required { get; set; }
}
