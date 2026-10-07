using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>One <c>Model/*.json</c> a referenced library ships (<c>[assembly: SparkLayer]</c>), as an incremental input.</summary>
[GenerateEquality]
public partial class LibraryModelFileInfo
{
    public string Assembly { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;
    public List<string> DependsOn { get; set; } = new();
}

/// <summary>One of the application's own <c>Model/*.json</c> AdditionalFiles.</summary>
[GenerateEquality]
public partial class AppModelFileInfo
{
    public string Path { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}
