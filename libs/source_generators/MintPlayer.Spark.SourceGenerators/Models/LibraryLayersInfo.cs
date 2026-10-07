using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>One AdditionalFile a library ships as a layer: its path under App_Data, its kind and its text.</summary>
[GenerateEquality]
public partial class LibraryLayerFileInfo
{
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    /// <summary>The file on disk, for a diagnostic's location.</summary>
    public string FullPath { get; set; } = string.Empty;
}

/// <summary>What the library-layer generator needs from the compilation itself.</summary>
[GenerateEquality]
public partial class LibraryLayersCompilationInfo
{
    /// <summary>A class library; an application (or any executable) embeds nothing (composition D10, S3).</summary>
    public bool IsLibrary { get; set; }

    /// <summary>The referenced assemblies that carry layers, which this library stacks above.</summary>
    public List<string> Dependencies { get; set; } = new();
}

/// <summary>What an application records about the layered libraries it references.</summary>
[GenerateEquality]
public partial class ApplicationLayersInfo
{
    public bool ShouldEmit { get; set; }

    /// <summary>The layered assemblies, in layer order.</summary>
    public List<string> Assemblies { get; set; } = new();

    public List<DuplicateAliasInfo> Duplicates { get; set; } = new();
}

[GenerateEquality]
public partial class DuplicateAliasInfo
{
    public string Alias { get; set; } = string.Empty;
    public string First { get; set; } = string.Empty;
    public string Second { get; set; } = string.Empty;
}
