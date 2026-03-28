namespace SparkEditor.Entities;

public class CustomActionDef
{
    public string? Id { get; set; }  // Generated: "CustomActionDefs/{name}"
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public string? ShowedOn { get; set; }
    public string? SelectionRule { get; set; }
    public bool RefreshOnCompleted { get; set; }
    public string? ConfirmationMessageKey { get; set; }
    public int Offset { get; set; }
}
