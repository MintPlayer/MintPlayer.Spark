namespace SparkEditor.Entities;

public class SecurityRightDef
{
    public string? Id { get; set; }
    public string Resource { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public bool IsDenied { get; set; }
    public bool IsImportant { get; set; }
}
