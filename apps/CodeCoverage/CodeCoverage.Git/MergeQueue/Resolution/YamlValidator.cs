using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// <c>yaml</c> (docs/code-coverage/dependency-updates-PRD.md R15): a YAML stream that parses, without a duplicate key in
/// any mapping, a non-scalar key, a tab in an indentation, an alias to an undefined anchor, more than
/// <see cref="YamlDocument.MaxAliases"/> aliases or more than <see cref="YamlDocument.MaxExpandedNodes"/> nodes once they
/// are expanded. Used bot-side on the dependency updater's patches, and nameable by a merge-queue rule for
/// <c>*.yml</c>/<c>*.yaml</c> files.
/// </summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed class YamlValidator : IContentValidator
{
    public string Name => MergeQueueWireRules.Yaml;

    public ContentVerdict Validate(string text) => YamlDocument.Parse(text).Verdict;
}
