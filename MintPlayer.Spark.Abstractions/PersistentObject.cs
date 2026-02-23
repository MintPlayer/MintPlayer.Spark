namespace MintPlayer.Spark.Abstractions;

public sealed class PersistentObject
{
    public string? Id { get; set; }
    public required string Name { get; set; }
    public required Guid ObjectTypeId { get; set; }
    public string? Breadcrumb { get; set; }
    public PersistentObjectAttribute[] Attributes { get; set; } = [];
}

public sealed class PersistentObjectAttribute
{
    public string? Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    public object? Value { get; set; }
    public string DataType { get; set; } = "string";
    public bool IsRequired { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsReadOnly { get; set; }
    public bool IsValueChanged { get; set; }
    public int Order { get; set; }
    public string? Query { get; set; }
    public string? Breadcrumb { get; set; }
    public EShowedOn ShowedOn { get; set; } = EShowedOn.Query | EShowedOn.PersistentObject;
    public ValidationRule[] Rules { get; set; } = [];

    public T? GetValue<T>()
    {
        if (Value is null) return default;
        return Convert.ChangeType(Value, typeof(T)) is T value ? value : default;
    }

    public void SetValue<T>(T? value) => Value = value;
}