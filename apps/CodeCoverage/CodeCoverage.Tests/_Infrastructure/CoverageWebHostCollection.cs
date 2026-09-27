using Xunit;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// Every test class that boots the real application belongs to this collection.
/// </summary>
/// <remarks>
/// <para>
/// Spark's registry, index catalog and model loader are process-wide, so two hosts starting
/// concurrently throw "Collection was modified; enumeration operation may not execute" (see
/// <see cref="CoverageWebHostFixture"/>). As collection fixtures, the hosts below are built once,
/// one after the other, and shared by every class here; <c>DisableParallelization</c> additionally
/// keeps this collection from running alongside any other, so no class elsewhere can boot a
/// composition root while one of these is starting.
/// </para>
/// <para>
/// ⚠️ A new HTTP test class joins this collection with <c>[Collection(Name)]</c>. It must not take
/// <c>IClassFixture&lt;CoverageWebHostFixture&gt;</c>: that would boot a host of its own.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CoverageWebHostCollection
    : ICollectionFixture<CoverageWebHostFixture>, ICollectionFixture<StubbedGitHubWebHostFixture>
{
    public const string Name = "Coverage web host";
}
