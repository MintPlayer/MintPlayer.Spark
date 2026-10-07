using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class TranslationsLibraryInfo
{
    public string FilePath { get; set; } = string.Empty;
    public bool Parsed { get; set; }
    public string ParseError { get; set; } = string.Empty;
    public List<TranslationsIssueInfo> Issues { get; set; } = new();
}

[GenerateEquality]
public partial class TranslationsIssueInfo
{
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
