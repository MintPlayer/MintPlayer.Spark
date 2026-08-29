using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Actions;

/// <summary>
/// Batching a set of ids is an optimization over the BASE read pipeline, so it must apply only
/// where it is invisible (#327 M2).
/// <para>
/// The hazard it guards: an actions class that overrides <c>OnLoadAsync</c> decorates the page it
/// returns. If a bulk path took the batched route anyway, that decoration would be skipped — and a
/// row's content would depend on how many rows the caller happened to select. That is the kind of
/// bug that only shows up in production, on the multi-select screen, months later.
/// </para>
/// </summary>
public class BatchedLoadOptOutTests
{
    private static readonly IEntityMapper Mapper = Substitute.For<IEntityMapper>();

    private sealed class PlainActions() : DefaultPersistentObjectActions<BatchProbe>(Mapper);

    private sealed class DecoratingActions() : DefaultPersistentObjectActions<BatchProbe>(Mapper)
    {
        public override async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
        {
            var result = await base.OnLoadAsync(id, parent);
            if (result is not null) result.Breadcrumb = "decorated";
            return result;
        }
    }

    /// <summary>A subclass that overrides something else entirely must still batch.</summary>
    private sealed class UnrelatedOverrideActions() : DefaultPersistentObjectActions<BatchProbe>(Mapper)
    {
        public override IReadOnlyCollection<string>? GetDefaultIncludes() => ["Something"];
    }

    [Fact]
    public void An_actions_class_that_does_not_override_the_load_hook_is_batched()
    {
        ((IBatchedLoadActions)new PlainActions()).SupportsBatchedLoad.Should().BeTrue();
    }

    [Fact]
    public void An_actions_class_that_overrides_the_load_hook_is_not_batched()
    {
        ((IBatchedLoadActions)new DecoratingActions()).SupportsBatchedLoad.Should().BeFalse(
            "the override decorates the page, and the batched path would skip it");
    }

    [Fact]
    public void Overriding_something_other_than_the_load_hook_does_not_opt_out_of_batching()
    {
        // GetDefaultIncludes participates IN the batched load rather than bypassing it, so opting
        // out for it would cost the round-trip saving for nothing.
        ((IBatchedLoadActions)new UnrelatedOverrideActions()).SupportsBatchedLoad.Should().BeTrue();
    }

    [Fact]
    public void The_base_class_itself_is_batched()
    {
        ((IBatchedLoadActions)new DefaultPersistentObjectActions<BatchProbe>(Mapper)).SupportsBatchedLoad
            .Should().BeTrue();
    }
}

public class BatchProbe
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}
