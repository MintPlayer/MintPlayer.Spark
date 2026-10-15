using Microsoft.Extensions.DependencyInjection;

namespace CodeCoverage.Git.MergeQueue.Resolution;

public static class MergeQueueResolutionServiceCollectionExtensions
{
    /// <summary>Registers every <c>[Register]</c>ed conflict resolver, content validator and the
    /// <see cref="ConflictResolverRegistry"/> over them (docs/coverage_merge_queue_PRD.md §10.3). The git writer calls it.</summary>
    public static IServiceCollection AddMergeQueueResolution(this IServiceCollection services)
        => services.AddMergeQueueResolutionServices();
}
