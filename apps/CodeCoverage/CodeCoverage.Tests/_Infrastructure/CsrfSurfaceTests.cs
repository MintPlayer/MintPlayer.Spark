using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// Every state-changing controller action must say, in its own source, whether it requires an
/// antiforgery token — and the ones that say "no" are enumerated here.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed in 11.0.0.</b> This test used to open by saying the app's antiforgery
/// configuration protects nothing, because <c>SparkAntiforgeryOptions.RequireAntiforgery</c> was
/// never assigned and the path-prefix branch is guarded on it. That is no longer true: the option
/// now defaults to <see langword="true"/>, so a mutating request under <c>/api</c> carrying an
/// ambient cookie is checked whether or not the action is annotated.
/// </para>
/// <para>
/// ⚠️ <b>This app still sets <c>WarnOnly = true</c></b> (<c>Program.cs</c>), which means an
/// <em>unannotated</em> endpoint is logged and allowed rather than rejected — deliberately, so the
/// affected surface shows up in one deploy instead of one production 400 at a time. That is exactly
/// why the second assertion below matters: if every mutating action is annotated explicitly, then
/// <c>WarnOnly</c> never decides anything here, and flipping it off later cannot break this app.
/// </para>
/// <para>
/// ⚠️ <b>Only <c>RequireAntiforgeryToken</c> counts.</b> MVC's <c>[ValidateAntiForgeryToken]</c>
/// implements <c>IFilterFactory, IOrderedFilter</c> — not the <c>IAntiforgeryMetadata</c> that
/// routing and Spark's gate read — so it compiles, reads as protection, and historically did
/// nothing (#300). On .NET 11 the two worlds are bridged in the other direction:
/// <c>[RequireAntiforgeryToken]</c> now also drives MVC's own rejecting filter, and applying both
/// attributes to one action throws at startup. So the right annotation is the one this test looks
/// for, and the wrong one is still worth failing on.
/// </para>
/// <para>
/// <b>The assertion is an exact set, not a bound</b> — the same shape as
/// <see cref="AnonymousSurfaceTests"/> and for the same reason: only equality fails when an exempt
/// action is <em>added</em>, and failing on an addition is the point.
/// </para>
/// <para>
/// ⚠️ <b>If this test fails, do not simply add the entry.</b> Adding a line here says a
/// state-changing endpoint may be called by any website a signed-in user visits. That is the last
/// step, after deciding the exemption is genuinely correct, and the comment beside it is where the
/// reason goes.
/// </para>
/// </remarks>
public class CsrfSurfaceTests
{
    /// <summary>
    /// Mutating actions carrying an explicit <c>[RequireAntiforgeryToken(false)]</c>, as
    /// <c>Controller.Action</c>.
    /// </summary>
    private static readonly string[] ExpectedExempt =
    [
        // Uploaded by CI runners bearing a covt_ API token or a GitHub workflow JWT, never by a
        // browser. Both schemes are named explicitly on the controller, so a cookie principal cannot
        // reach these at all — there is no ambient authority for a forged request to ride.
        "UploadsController.Upload",
        "UploadsController.Finish",

        // Anonymous by design: fork PRs run without credentials, so there is no session to forge.
        "ForkUploadsController.UploadFromFork",
    ];

    /// <summary>
    /// The exempt set is exactly what it claims to be — nothing has quietly joined it.
    /// </summary>
    [Fact]
    public void Only_the_listed_actions_are_exempt_from_antiforgery()
    {
        var actual = MutatingActions()
            .Where(m => Metadata(m) is { RequiresValidation: false })
            .Select(Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([.. ExpectedExempt.OrderBy(n => n, StringComparer.Ordinal)], actual);
    }

    /// <summary>
    /// ⚠️ The load-bearing one. Every mutating action must state its position explicitly, so that
    /// none of them depends on <c>WarnOnly</c>, on the path-prefix list, or on the framework default
    /// staying where it is. An action that says nothing is an action whose protection is decided
    /// somewhere else, by configuration a reader of this file cannot see.
    /// </summary>
    [Fact]
    public void Every_state_changing_action_states_its_antiforgery_position()
    {
        var unannotated = MutatingActions()
            .Where(m => Metadata(m) is null)
            .Select(Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unannotated);
    }

    /// <summary>
    /// ⚠️ Guards the guard. MVC's attribute would satisfy a reviewer and not the middleware, and on
    /// .NET 11 combining it with the effective one throws at startup.
    /// </summary>
    [Fact]
    public void No_controller_uses_MVCs_ineffective_antiforgery_attribute()
    {
        var misannotated = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name is "ValidateAntiForgeryTokenAttribute" or "AutoValidateAntiforgeryTokenAttribute"))
            .Select(Name)
            .ToArray();

        Assert.Empty(misannotated);
    }

    private static IEnumerable<MethodInfo> MutatingActions() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(IsStateChanging);

    /// <summary>Action-level annotation wins over controller-level, matching how routing merges metadata.</summary>
    private static IAntiforgeryMetadata? Metadata(MethodInfo method) =>
        method.GetCustomAttribute<RequireAntiforgeryTokenAttribute>()
        ?? (IAntiforgeryMetadata?)method.DeclaringType!.GetCustomAttribute<RequireAntiforgeryTokenAttribute>();

    private static string Name(MethodInfo method) => $"{method.DeclaringType!.Name}.{method.Name}";

    private static bool IsStateChanging(MethodInfo method) =>
        method.GetCustomAttributes().Any(a => a is HttpPostAttribute or HttpPutAttribute or HttpPatchAttribute or HttpDeleteAttribute);
}
