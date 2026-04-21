namespace MintPlayer.Spark.Abstractions;

public sealed class PersistentObject
{
    public string? Id { get; set; }
    public required string Name { get; set; }
    public required Guid ObjectTypeId { get; set; }
    public string? Breadcrumb { get; set; }
    public PersistentObjectAttribute[] Attributes { get; set; } = [];

    /// <summary>
    /// Optimistic-concurrency token. Populated by the server on read (RavenDB's change
    /// vector for the underlying entity). Clients should echo the value back on update —
    /// if the server's current change vector differs, the update is rejected with HTTP 409.
    /// Null on create and when the caller doesn't opt in.
    /// </summary>
    public string? Etag { get; set; }
}

public sealed class PersistentObjectAttribute
{
    public string? Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    public object? Value { get; set; }
    public string DataType { get; set; } = "string";
    public bool IsArray { get; set; }
    public bool IsRequired { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsReadOnly { get; set; }
    public bool IsValueChanged { get; set; }
    public int Order { get; set; }
    public string? Query { get; set; }
    public string? Breadcrumb { get; set; }
    public EShowedOn ShowedOn { get; set; } = EShowedOn.Query | EShowedOn.PersistentObject;
    public ValidationRule[] Rules { get; set; } = [];
    public Guid? Group { get; set; }
    public string? Renderer { get; set; }
    public Dictionary<string, object>? RendererOptions { get; set; }

    public T? GetValue<T>()
    {
        if (Value is null) return default;
        return Convert.ChangeType(Value, typeof(T)) is T value ? value : default;
    }

    public void SetValue<T>(T? value) => Value = value;
}