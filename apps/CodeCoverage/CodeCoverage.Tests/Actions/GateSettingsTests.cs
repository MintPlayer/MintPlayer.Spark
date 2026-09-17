using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.Actions;

/// <summary>
/// The coverage gate as it is edited through the Spark PO form, after #413 replaced the
/// hand-written card and its REST endpoints.
/// </summary>
/// <remarks>
/// Two properties are load-bearing and neither is obvious from reading the code:
/// <list type="number">
/// <item>
/// The gate's rules are enforced on <c>Repository</c>, because a gate is embedded and never saves on
/// its own. <c>GateSettingsActions.OnRefreshAsync</c> can make a field required, but Spark
/// re-derives refresh rules on save only for a ROOT type — so if
/// <c>RepositoryActions.OnBeforeSaveAsync</c> stopped checking, a client that skipped
/// <c>/spark/po/refresh</c> would save a contradictory gate and nothing would notice.
/// </item>
/// <item>
/// The lookup keys are the stored values, lowercase. If these ever become enum members, RavenDB
/// starts writing <c>"Auto"</c> into <c>Repositories.Gate</c> and into the <c>GateSnapshot</c> of
/// every published build, and diverges from the <c>coverage.yml</c> vocabulary in other people's
/// repositories — which cannot be migrated.
/// </item>
/// </list>
/// </remarks>
public class GateSettingsTests
{
    // ---- the lookups are the stored vocabulary ----------------------------------------------

    [Fact]
    public void The_comparison_lookup_keys_are_the_stored_lowercase_values()
    {
        var keys = ProjectComparison.Items.Select(i => i.Key).ToArray();

        Assert.Equal(["auto", "fixed"], keys);
    }

    [Fact]
    public void The_basis_lookup_keys_are_the_stored_lowercase_values()
    {
        var keys = LookupReferences.ProjectBasis.Items.Select(i => i.Key).ToArray();

        Assert.Equal(["scoped", "projection"], keys);
    }

    [Fact]
    public void A_default_gate_carries_the_keys_the_lookups_offer()
    {
        // The entity's defaults and the dropdown's options are two declarations of one vocabulary.
        // A default that no option matches renders as an empty dropdown on an untouched gate.
        var gate = new GateSettings();

        Assert.Contains(ProjectComparison.Items, i => i.Key == gate.ProjectMode);
        Assert.Contains(LookupReferences.ProjectBasis.Items, i => i.Key == gate.ProjectBasis);
    }

    [Fact]
    public void The_basis_lookup_does_not_offer_the_evaluators_derived_value()
    {
        // GateEvaluator substitutes "whole" for a build that is not partial. It is a derived state,
        // never a choice — offering it would let a user set something the evaluator overwrites.
        Assert.DoesNotContain(LookupReferences.ProjectBasis.Items, i => i.Key == "whole");
    }

    // ---- the refresh hook shapes the form ----------------------------------------------------

    [Theory]
    [InlineData("fixed", true)]
    [InlineData("auto", false)]
    public async Task The_project_target_is_shown_only_for_a_fixed_comparison(string mode, bool expected)
    {
        var obj = GatePo(mode);

        await Create<GateSettingsActions>()
            .OnRefreshAsync(RefreshArgs(obj));

        Assert.Equal(expected, obj[nameof(GateSettings.ProjectTarget)].IsVisible);
        Assert.Equal(expected, obj[nameof(GateSettings.ProjectTarget)].IsRequired);
    }

    [Fact]
    public async Task The_allowed_drop_stays_visible_in_both_modes()
    {
        // It reads like an "auto"-only setting and it is not: GateEvaluator judges
        // `headRate >= baseRate - ProjectThreshold` in both modes, where baseRate is the fixed
        // target under "fixed". Hiding it would hide a number that is still in force.
        var fixedMode = GatePo("fixed");
        var actions = Create<GateSettingsActions>();

        await actions.OnRefreshAsync(RefreshArgs(fixedMode));

        Assert.True(fixedMode[nameof(GateSettings.ProjectThreshold)].IsVisible);
    }

    [Fact]
    public async Task The_hook_re_establishes_state_rather_than_patching_the_last_call()
    {
        // Every invocation is handed a freshly scaffolded object, so a handler with an `if` and no
        // `else` leaves the target visible forever after one visit to fixed mode. Asserting the
        // round trip is what catches that.
        var actions = Create<GateSettingsActions>();

        var toFixed = GatePo("fixed");
        await actions.OnRefreshAsync(RefreshArgs(toFixed));
        Assert.True(toFixed[nameof(GateSettings.ProjectTarget)].IsVisible);

        var backToAuto = GatePo("auto");
        await actions.OnRefreshAsync(RefreshArgs(backToAuto));
        Assert.False(backToAuto[nameof(GateSettings.ProjectTarget)].IsVisible);
    }

    // ---- the save-time rules, which are the actual guarantee ---------------------------------

    [Theory]
    [InlineData("nonsense", "scoped", null, null, 0d, 0d)]
    [InlineData("auto", "nonsense", null, null, 0d, 0d)]
    [InlineData("auto", "scoped", 101.0, null, 0d, 0d)]
    [InlineData("auto", "scoped", null, 101.0, 0d, 0d)]
    [InlineData("auto", "scoped", null, null, 101.0, 0d)]
    [InlineData("auto", "scoped", null, null, 0d, 101.0)]
    [InlineData("fixed", "scoped", null, null, 0d, 0d)]
    public async Task An_invalid_gate_is_refused_on_save(
        string projectMode, string projectBasis,
        double? projectTarget, double? patchTarget, double projectThreshold, double patchThreshold)
    {
        // These cases came over verbatim from RepoSettingsController.PutGate, which #413 deleted.
        // They must keep failing somewhere, or replacing a validated endpoint with the generic PO
        // save would be a regression wearing a refactor's clothes.
        var repository = new Repository
        {
            OwnerLogin = "someone",
            Gate = new GateSettings
            {
                ProjectMode = projectMode,
                ProjectBasis = projectBasis,
                ProjectTarget = projectTarget,
                PatchTarget = patchTarget,
                ProjectThreshold = projectThreshold,
                PatchThreshold = patchThreshold,
            },
        };

        await Assert.ThrowsAsync<SparkValidationException>(
            () => Actions().OnBeforeSaveAsync(Po(), repository));
    }

    [Fact]
    public async Task A_valid_gate_is_accepted()
    {
        var repository = new Repository
        {
            OwnerLogin = "someone",
            Gate = new GateSettings
            {
                ProjectMode = ProjectComparison.Fixed,
                ProjectBasis = LookupReferences.ProjectBasis.Projection,
                ProjectTarget = 80,
                ProjectThreshold = 1,
                PatchThreshold = 1,
            },
        };

        await Actions().OnBeforeSaveAsync(Po(), repository);
    }

    [Fact]
    public async Task A_repository_with_no_gate_saves_untouched()
    {
        // An absent gate means "every default", which is how most repositories sit. It must not be
        // dragged through the percentage checks.
        await Actions().OnBeforeSaveAsync(
            Po(), new Repository { OwnerLogin = "someone", Gate = null });
    }

    [Fact]
    public async Task The_fixed_target_rule_names_the_attribute_it_belongs_to()
    {
        // The message lands on the field, so the person at the screen sees it under the input they
        // have to fix rather than at the top of the form.
        var repository = new Repository
        {
            OwnerLogin = "someone",
            Gate = new GateSettings { ProjectMode = ProjectComparison.Fixed, ProjectTarget = null },
        };

        var ex = await Assert.ThrowsAsync<SparkValidationException>(
            () => Actions().OnBeforeSaveAsync(Po(), repository));

        Assert.Equal(nameof(GateSettings.ProjectTarget), ex.AttributeName);
    }

    /// <summary>
    /// Builds a real actions class through its generated constructor, resolving parameters by type
    /// so an added <c>[Inject]</c> field does not break this. Nothing here reaches a dependency —
    /// the gate rules read the entity and nothing else — so a substitute that starts being used
    /// fails loudly rather than passing against a stub.
    /// </summary>
    private static T Create<T>()
    {
        var ctor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p =>
            {
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();
        return (T)ctor.Invoke(args);
    }

    private static RepositoryActions Actions() => Create<RepositoryActions>();

    private static PersistentObject Po() => new()
    {
        Name = "GateSettings",
        ObjectTypeId = Guid.Empty,
    };

    private static PersistentObject GatePo(string mode) => new()
    {
        Name = "GateSettings",
        ObjectTypeId = Guid.Empty,
        Attributes =
        [
            new PersistentObjectAttribute { Name = nameof(GateSettings.ProjectMode), Value = mode },
            new PersistentObjectAttribute { Name = nameof(GateSettings.ProjectTarget) },
            new PersistentObjectAttribute { Name = nameof(GateSettings.ProjectThreshold) },
        ],
    };

    private static SparkRefreshArgs<GateSettings> RefreshArgs(PersistentObject obj) =>
        (SparkRefreshArgs<GateSettings>)Activator.CreateInstance(
            typeof(SparkRefreshArgs<GateSettings>),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [obj, obj[nameof(GateSettings.ProjectMode)], false, CancellationToken.None],
            culture: null)!;
}
