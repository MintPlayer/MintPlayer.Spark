using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #301 — <see cref="ISparkRowRule{T}"/>, the row rule made reachable from an application's own
/// controllers and jobs.
/// <para>
/// The shape suggested in the issue — a bare <c>FilterAsync(action)</c> returning a predicate — is
/// the one shape not to ship, and the first test here is why: <c>GetRowFilterAsync</c> returns
/// <c>null</c> for a type that expresses its policy through <c>IsAllowedAsync</c> alone, so a caller
/// consuming only the filter sees every row while believing it applied the rule.
/// </para>
/// </summary>
public class SparkRowRuleTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("7b0a11aa-11aa-11aa-11aa-7b0a11aa11aa");

    private SparkEndpointFactory<RowRuleContext> _factory = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<RowRuleContext>(Store, [GuardedDocModel.For(DocTypeId)]);
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedAsync(params object[] entities)
        => await base.SeedAsync(async session =>
        {
            foreach (var e in entities) await session.StoreAsync(e);
        });

    [Fact]
    public async Task ApplyAsync_applies_the_filter_and_the_per_row_hook()
    {
        // THE discriminating check for D4. GuardedDocActions overrides IsAllowedAsync and nothing
        // else, so its GetRowFilterAsync returns null — "unrestricted" — and a FilterAsync-shaped
        // API would hand this caller every row.
        await SeedAsync(
            new GuardedDoc { Id = "docs/a", Name = "A", IsVisible = true },
            new GuardedDoc { Id = "docs/b", Name = "B", IsVisible = false });

        var rule = _factory.GetService<ISparkRowRule<GuardedDoc>>();
        var session = _factory.GetService<IAsyncDocumentSession>();

        var rows = await rule.ApplyAsync(session.Query<GuardedDoc>(), "Query");

        rows.Select(d => d.Id).Should().BeEquivalentTo(["docs/a"]);
    }

    [Fact]
    public async Task GetFilterAsync_returns_null_for_a_hook_only_type()
    {
        // Documented, and documented for a reason: null means UNRESTRICTED, and here it means it
        // while the type is in fact restricted. This pins the trap rather than the fix.
        await SeedAsync(new GuardedDoc { Id = "docs/a", Name = "A", IsVisible = true });

        var rule = _factory.GetService<ISparkRowRule<GuardedDoc>>();

        (await rule.GetFilterAsync("Query")).Should().BeNull();
    }

    [Fact]
    public async Task GetFilterAsync_returns_the_expression_for_a_filter_type()
    {
        await SeedAsync(new RowRuleLedger { Id = "ledgers/a", Owner = "alice", Amount = 1 });

        var rule = _factory.GetService<ISparkRowRule<RowRuleLedger>>();

        var filter = await rule.GetFilterAsync("Query");

        filter.Should().NotBeNull();
        filter!.Compile()(new RowRuleLedger { Owner = RowRuleLedgerActions.CurrentOwner }).Should().BeTrue();
        filter.Compile()(new RowRuleLedger { Owner = "someone-else" }).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyAsync_pushes_the_filter_into_the_query()
    {
        // The point of expressing the rule as an expression: a scoped type must read its own rows,
        // not the collection. Asserted on the emitted RQL, because a correct row count is equally
        // consistent with reading everything and discarding most of it in memory.
        await SeedAsync(
            new RowRuleLedger { Id = "ledgers/a", Owner = RowRuleLedgerActions.CurrentOwner, Amount = 1 },
            new RowRuleLedger { Id = "ledgers/b", Owner = "someone-else", Amount = 2 });

        var rql = new List<string>();
        Store.OnBeforeQuery += (_, e) => rql.Add(e.QueryCustomization.ToString()!);

        var rule = _factory.GetService<ISparkRowRule<RowRuleLedger>>();
        var session = _factory.GetService<IAsyncDocumentSession>();

        var rows = await rule.ApplyAsync(session.Query<RowRuleLedger>(), "Query");

        rows.Select(l => l.Id).Should().BeEquivalentTo(["ledgers/a"]);
        rql.Should().ContainSingle().Which.Should().Contain("Owner");
    }

    [Fact]
    public async Task ApplyAsync_returns_everything_for_a_type_with_no_rule()
    {
        await SeedAsync(
            new RowRuleOpen { Id = "opens/a", Name = "A" },
            new RowRuleOpen { Id = "opens/b", Name = "B" });

        var rule = _factory.GetService<ISparkRowRule<RowRuleOpen>>();
        var session = _factory.GetService<IAsyncDocumentSession>();

        var rows = await rule.ApplyAsync(session.Query<RowRuleOpen>(), "Query");

        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task IsAllowedAsync_answers_for_one_loaded_row()
    {
        var rule = _factory.GetService<ISparkRowRule<GuardedDoc>>();

        (await rule.IsAllowedAsync("Read", new GuardedDoc { IsVisible = true })).Should().BeTrue();
        (await rule.IsAllowedAsync("Read", new GuardedDoc { IsVisible = false })).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_derives_the_single_row_check_from_the_filter()
    {
        // A filter-only type still answers a detail check, by compiling the same expression. One
        // source of truth is what keeps a list and a detail screen from disagreeing.
        var rule = _factory.GetService<ISparkRowRule<RowRuleLedger>>();

        (await rule.IsAllowedAsync("Read", new RowRuleLedger { Owner = RowRuleLedgerActions.CurrentOwner }))
            .Should().BeTrue();
        (await rule.IsAllowedAsync("Read", new RowRuleLedger { Owner = "someone-else" }))
            .Should().BeFalse();
    }

    [Fact]
    public async Task GetProtectedAttributesAsync_reports_what_the_actions_class_protects()
    {
        var rule = _factory.GetService<ISparkRowRule<RowRuleLedger>>();

        var protectedNames = await rule.GetProtectedAttributesAsync(
            "Read", new RowRuleLedger { Owner = "someone-else" });

        protectedNames.Should().BeEquivalentTo(["Amount"]);
        (await rule.GetProtectedAttributesAsync("Read", new RowRuleLedger { Owner = RowRuleLedgerActions.CurrentOwner }))
            .Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_over_a_projection_filters_by_reloading_the_base_documents()
    {
        // S4. The rule is typed on the document, so it cannot compose into an index projection —
        // ComposeRowFilterAsync declines and the documents behind the page are batch-loaded and
        // judged instead. This is the first shape a real consumer hits: CodeCoverage queries
        // session.Query<VRepository, Repositories_Overview>() exactly like this.
        await Store.ExecuteIndexAsync(new RowRuleLedgers_Overview());
        await SeedAsync(
            new RowRuleLedger { Id = "ledgers/a", Owner = RowRuleLedgerActions.CurrentOwner, Amount = 1 },
            new RowRuleLedger { Id = "ledgers/b", Owner = "someone-else", Amount = 2 });
        await Store.WaitForIndexingAsync();

        var rule = _factory.GetService<ISparkRowRule<RowRuleLedger>>();
        var session = _factory.GetService<IAsyncDocumentSession>();

        var rows = await rule.ApplyAsync(
            session.Query<VRowRuleLedger, RowRuleLedgers_Overview>().ProjectInto<VRowRuleLedger>(),
            "Query");

        rows.Select(v => v.Id).Should().BeEquivalentTo(["ledgers/a"]);
    }

    [Fact]
    public async Task Hooks_are_invoked_once_per_type_and_action_per_request()
    {
        // F7: the memo bounds hook invocations to one per (type, action) per request because
        // RavenDB caps a session at 30 requests. A public facade with its own memo would double
        // them on any request that touched both a controller and /spark, silently.
        await SeedAsync(
            new RowRuleLedger { Id = "ledgers/a", Owner = RowRuleLedgerActions.CurrentOwner, Amount = 1 },
            new RowRuleLedger { Id = "ledgers/b", Owner = RowRuleLedgerActions.CurrentOwner, Amount = 2 });

        RowRuleLedgerActions.FilterInvocations = 0;

        var rule = _factory.GetService<ISparkRowRule<RowRuleLedger>>();
        var rowSecurity = _factory.GetService<IRowSecurity>();
        var session = _factory.GetService<IAsyncDocumentSession>();

        await rule.ApplyAsync(session.Query<RowRuleLedger>(), "Query");
        await rule.GetFilterAsync("Query");
        // The pipeline's own path, in the same scope — this is the combination that would double.
        await rowSecurity.ComposeRowFilterAsync(
            session.Query<RowRuleLedger>(), typeof(RowRuleLedger), typeof(RowRuleLedger), "Query");

        RowRuleLedgerActions.FilterInvocations.Should().Be(1);
    }
}

/// <summary>A filter-only rule: the half of the policy <c>GuardedDocActions</c> does not exercise.</summary>
public class RowRuleLedger
{
    public string? Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class RowRuleLedgerActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<RowRuleLedger>(entityMapper)
{
    internal const string CurrentOwner = "alice";

    /// <summary>Counts real hook invocations, so the per-request memo is observable.</summary>
    internal static int FilterInvocations;

    public override Task<Expression<Func<RowRuleLedger, bool>>?> GetRowFilterAsync(string action)
    {
        Interlocked.Increment(ref FilterInvocations);
        return Task.FromResult<Expression<Func<RowRuleLedger, bool>>?>(l => l.Owner == CurrentOwner);
    }

    public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, RowRuleLedger entity)
        => Task.FromResult<IReadOnlyCollection<string>?>(entity.Owner == CurrentOwner ? null : ["Amount"]);
}

/// <summary>No rule at all — the case that must cost nothing and hide nothing.</summary>
public class RowRuleOpen
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class RowRuleLedgers_Overview : Raven.Client.Documents.Indexes.AbstractIndexCreationTask<RowRuleLedger>
{
    public RowRuleLedgers_Overview()
    {
        Map = ledgers => from ledger in ledgers
                         select new { ledger.Owner, ledger.Amount };
        StoreAllFields(Raven.Client.Documents.Indexes.FieldStorage.Yes);
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
    }
}

[FromIndex(typeof(RowRuleLedgers_Overview))]
public class VRowRuleLedger
{
    public string? Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class RowRuleContext : SparkContext
{
    public IRavenQueryable<GuardedDoc> Docs => Session.Query<GuardedDoc>();
    public IRavenQueryable<RowRuleLedger> Ledgers => Session.Query<RowRuleLedger>();
    public IRavenQueryable<RowRuleOpen> Opens => Session.Query<RowRuleOpen>();
}
