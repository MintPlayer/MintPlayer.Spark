using MintPlayer.Spark.Actions;

namespace MintPlayer.Spark.Tests.Actions;

/// <summary>
/// Pins the default behaviour and override contract of the row-level
/// <see cref="DefaultPersistentObjectActions{T}.IsAllowedAsync(string, T)"/> hook introduced
/// in the security audit. The default is permissive (returns true) — an application that
/// wants row-level enforcement must override explicitly, which is a review-time signal.
/// </summary>
public class DefaultPersistentObjectActionsIsAllowedTests
{
    public class Widget { public string? Id { get; set; } }

    // The base class's source-generated constructor requires an IEntityMapper; IsAllowedAsync
    // doesn't use it, so null! is safe here.
    private sealed class DefaultActions : DefaultPersistentObjectActions<Widget>
    {
        public DefaultActions() : base(null!) { }
    }

    private sealed class DenyAllActions : DefaultPersistentObjectActions<Widget>
    {
        public DenyAllActions() : base(null!) { }
        public override Task<bool> IsAllowedAsync(string action, Widget entity) => Task.FromResult(false);
    }

    private sealed class DenyDeleteActions : DefaultPersistentObjectActions<Widget>
    {
        public DenyDeleteActions() : base(null!) { }
        public override Task<bool> IsAllowedAsync(string action, Widget entity)
            => Task.FromResult(action != "Delete");
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Query")]
    [InlineData("Edit")]
    [InlineData("Delete")]
    [InlineData("New")]
    public async Task Default_implementation_allows_every_standard_action(string action)
    {
        var actions = new DefaultActions();
        var result = await actions.IsAllowedAsync(action, new Widget());
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Override_can_deny_wholesale()
    {
        var actions = new DenyAllActions();
        var result = await actions.IsAllowedAsync("Read", new Widget());
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Override_can_deny_selectively_per_action()
    {
        var actions = new DenyDeleteActions();

        (await actions.IsAllowedAsync("Read", new Widget())).Should().BeTrue();
        (await actions.IsAllowedAsync("Edit", new Widget())).Should().BeTrue();
        (await actions.IsAllowedAsync("Delete", new Widget())).Should().BeFalse();
    }
}
