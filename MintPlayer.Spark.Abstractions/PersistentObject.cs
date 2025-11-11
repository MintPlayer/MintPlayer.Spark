namespace MintPlayer.Spark.Abstractions;

public sealed class PersistentObject
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ClrType { get; set; }
    public PersistentObjectAttribute[] Attributes { get; set; } = [];
}

public sealed class PersistentObjectAttribute
{
    private object? _value;

    public required Guid Id { get; set; }
    public required string Name { get; set; }

    public T? GetValue<T>()
        => Convert.ChangeType(_value, typeof(T?)) is T value ? value : default;
}