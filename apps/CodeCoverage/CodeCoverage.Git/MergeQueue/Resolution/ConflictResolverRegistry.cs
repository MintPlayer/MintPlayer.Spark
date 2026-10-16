using MintPlayer.SourceGenerators.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// The merge queue's resolvers and validators by name (docs/coverage_merge_queue_PRD.md §10.3). MintPlayer.SourceGenerators
/// 13.0.0's <c>[Register]</c> has no keyed overload, so the registry is built over the <c>IEnumerable&lt;&gt;</c> of each
/// interface. Two implementations with one name are a programming error and throw at construction, so a writer that
/// starts serves exactly one meaning per name. Its name sets equal <see cref="MergeQueueWireRules.Strategies"/> and
/// <see cref="MergeQueueWireRules.Validators"/> (a test holds them together, D57): whatever the parser accepts, the writer
/// can run.
/// </summary>
[Register(ServiceLifetime.Singleton)]
public sealed class ConflictResolverRegistry
{
    private readonly Dictionary<string, IConflictResolver> resolvers;
    private readonly Dictionary<string, IContentValidator> validators;

    public ConflictResolverRegistry(IEnumerable<IConflictResolver> resolvers, IEnumerable<IContentValidator> validators)
    {
        this.resolvers = ByName(resolvers, r => r.Strategy, "resolver");
        this.validators = ByName(validators, v => v.Name, "validator");
    }

    /// <summary>The built-in set, as the writer's DI registers it; for code that runs the resolver without a container.</summary>
    public static ConflictResolverRegistry Default { get; } = new(BuiltInResolvers(), BuiltInValidators());

    /// <summary>Every <see cref="IConflictResolver"/> of this assembly. Kept in step with the <c>[Register]</c>ed set by
    /// <c>ConflictResolverRegistryTests</c>.</summary>
    public static IEnumerable<IConflictResolver> BuiltInResolvers() => [new TakeBaseResolver(), new KeepBothResolver(), new LineSetResolver(), new EvictResolver()];

    /// <summary>Every <see cref="IContentValidator"/> of this assembly.</summary>
    public static IEnumerable<IContentValidator> BuiltInValidators()
        => [new Utf8Validator(), new JsonValidator(), new JsonNoDuplicateKeysValidator(), new XmlValidator(), new NoConflictMarkersValidator(),
            new YamlValidator(), new GitHubWorkflowValidator()];

    public IReadOnlyCollection<string> StrategyNames => resolvers.Keys;

    public IReadOnlyCollection<string> ValidatorNames => validators.Keys;

    /// <summary>The resolver named <paramref name="strategy"/>; throws for an unknown name, which the wire validation
    /// refuses before a job starts.</summary>
    public IConflictResolver Resolver(string strategy)
        => resolvers.TryGetValue(strategy, out var resolver) ? resolver : throw new KeyNotFoundException($"No conflict resolver is named '{strategy}'.");

    public IContentValidator Validator(string name)
        => validators.TryGetValue(name, out var validator) ? validator : throw new KeyNotFoundException($"No content validator is named '{name}'.");

    private static Dictionary<string, T> ByName<T>(IEnumerable<T> items, Func<T, string> name, string kind)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!map.TryAdd(name(item), item))
                throw new InvalidOperationException($"Two {kind}s are named '{name(item)}'.");
        }
        return map;
    }
}
