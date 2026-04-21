using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

[AutoValueComparer]
public partial class PersistentObjectInfo
{
    public string EntityName { get; set; } = string.Empty;
    public string EntityFullName { get; set; } = string.Empty;
    public List<string> AttributeNames { get; set; } = new();
}
