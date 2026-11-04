using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>One map of the generated <c>Spark_Search</c> index.</summary>
public sealed class SearchMapInfo : System.IEquatable<SearchMapInfo>
{
    /// <summary>The model's entity type name, written as a string literal.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>The CLR type, fully qualified from its symbol.</summary>
    public string EntityFullName { get; set; } = string.Empty;

    /// <summary>The text properties, names from their symbols.</summary>
    public List<SearchFieldInfo> Fields { get; set; } = new();

    /// <summary>Whether the type has a boolean <c>IsDeleted</c>.</summary>
    public bool HasIsDeleted { get; set; }

    public bool Equals(SearchMapInfo? other)
        => other is not null
            && EntityName == other.EntityName
            && EntityFullName == other.EntityFullName
            && HasIsDeleted == other.HasIsDeleted
            && Fields.SequenceEqual(other.Fields);

    public override bool Equals(object? obj) => Equals(obj as SearchMapInfo);

    public override int GetHashCode() => (EntityName, EntityFullName).GetHashCode();
}

/// <summary>A text property of a searchable type.</summary>
public sealed class SearchFieldInfo : System.IEquatable<SearchFieldInfo>
{
    public string Name { get; set; } = string.Empty;
    public bool IsCollection { get; set; }

    public bool Equals(SearchFieldInfo? other) => other is not null && Name == other.Name && IsCollection == other.IsCollection;
    public override bool Equals(object? obj) => Equals(obj as SearchFieldInfo);
    public override int GetHashCode() => Name.GetHashCode();
}
