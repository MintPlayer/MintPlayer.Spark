using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

public sealed class PersistentObject
{
    private readonly List<PersistentObjectAttribute> _attributes = [];

    public string? Id { get; set; }
    public required string Name { get; set; }
    public required Guid ObjectTypeId { get; set; }
    public string? Breadcrumb { get; set; }

    /// <summary>
    /// Optimistic-concurrency token. Populated by the server on read (RavenDB's change
    /// vector for the underlying entity). Clients should echo the value back on update —
    /// if the server's current change vector differs, the update is rejected with HTTP 409.
    /// Null on create and when the caller doesn't opt in.
    /// </summary>
    public string? Etag { get; set; }

    /// <summary>
    /// The attributes on this PersistentObject. Read-only after construction —
    /// mutation goes through <see cref="AddAttribute"/> (framework-internal) or
    /// <see cref="PersistentObjectAttribute.CloneAndAdd"/>.
    /// </summary>
    /// <remarks>
    /// The <c>init</c> setter routes incoming arrays through <see cref="AddAttribute"/>
    /// so that <see cref="PersistentObjectAttribute.Parent"/> is always set — whether
    /// callers construct via object-initializer (<c>new PersistentObject { Attributes = [...] }</c>),
    /// STJ deserializes off the wire, or framework code scaffolds from schema.
    /// </remarks>
    public IReadOnlyList<PersistentObjectAttribute> Attributes
    {
        get => _attributes;
        init
        {
            _attributes.Clear();
            if (value is null) return;
            foreach (var attribute in value)
                AddAttribute(attribute);
        }
    }

    /// <summary>
    /// Single mutation point for the attributes collection. Sets the child's
    /// <see cref="PersistentObjectAttribute.Parent"/> back-reference and appends
    /// to the backing list. Called by framework code (EntityMapper, SyncActionHandler),
    /// by <see cref="PersistentObjectAttribute.CloneAndAdd"/>, and by the
    /// <see cref="Attributes"/> init setter (the path for object-initializer and
    /// JSON-deserialization construction).
    /// </summary>
    internal void AddAttribute(PersistentObjectAttribute attribute)
    {
        attribute.Parent = this;
        _attributes.Add(attribute);
    }
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

    /// <summary>
    /// The PersistentObject that owns this attribute. Set by
    /// <see cref="PersistentObject.AddAttribute"/>; never null once an attribute
    /// has been added to a PO. Not serialized (would create a JSON cycle).
    /// </summary>
    [JsonIgnore]
    public PersistentObject Parent { get; internal set; } = null!;

    public T? GetValue<T>()
    {
        if (Value is null) return default;
        return Convert.ChangeType(Value, typeof(T)) is T value ? value : default;
    }

    public void SetValue<T>(T? value) => Value = value;
}
