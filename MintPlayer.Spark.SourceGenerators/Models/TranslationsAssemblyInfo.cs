using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

[AutoValueComparer]
public partial class TranslationsAssemblyInfo
{
    public string AssemblyName { get; set; } = string.Empty;
    public List<TranslationsChunkInfo> Chunks { get; set; } = new();
}

[AutoValueComparer]
public partial class TranslationsChunkInfo
{
    public int ChunkIndex { get; set; }
    public int ChunkCount { get; set; }
    public string Json { get; set; } = string.Empty;
}

[AutoValueComparer]
public partial class TranslationsHostEntry
{
    public string Key { get; set; } = string.Empty;
    public List<TranslationsLanguageEntry> Languages { get; set; } = new();
}

[AutoValueComparer]
public partial class TranslationsLanguageEntry
{
    public string Language { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

[AutoValueComparer]
public partial class TranslationsAggregateInfo
{
    public bool ShouldEmit { get; set; }
    public List<TranslationsAssemblyInfo> Assemblies { get; set; } = new();
    public TranslationsAssemblyInfo? OwnAssembly { get; set; }
    public string OwnAssemblyName { get; set; } = string.Empty;
}

[AutoValueComparer]
public partial class TranslationsConflict
{
    public string Key { get; set; } = string.Empty;
    public string WinnerAssembly { get; set; } = string.Empty;
    public string LoserAssembly { get; set; } = string.Empty;
}
