using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// The exact set of controller actions reachable without authentication.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This exists because nothing else records it.</b> M16 assumed the anonymous surface would
/// show up in <c>App_Data/securityPosture.txt</c>; it does not. That file is eleven lines of Spark
/// rights computed from <c>security.json</c> (<c>Browse/Coverage</c>, <c>Query/…</c>, <c>Read/…</c>)
/// and lists no MVC endpoints at all — <c>BadgeController</c>'s long-standing
/// <c>[AllowAnonymous]</c> does not appear in it either. So adding a new anonymous endpoint moves
/// nothing in any reviewed artefact, and the CI gate that exists for security posture would stay
/// green through it.
/// </para>
/// <para>
/// <b>The assertion is an exact set, not a bound.</b> A "no more than N" test passes while an
/// endpoint is swapped for a different one, and a "these are present" test passes while ten more
/// are added beside them. Equality is the only shape that makes an addition fail — and failing on
/// an addition is the entire point: the reviewer is meant to be forced to write the new line down.
/// </para>
/// <para>
/// ⚠️ <b>If this test fails, do not simply add the entry.</b> It failing means a way into this
/// application without credentials was created or removed. Adding the line is the last step, after
/// deciding the endpoint should exist, and the comment beside the entry is where the reason goes.
/// </para>
/// </remarks>
public class AnonymousSurfaceTests
{
    /// <summary>
    /// Every action that may be called with no credential, as <c>Controller.Action</c>.
    /// </summary>
    private static readonly string[] Expected =
    [
        // Badge SVGs are embedded in READMEs, which are rendered by GitHub's image proxy with no
        // credential of ours. Private repositories are gated inside the action by a per-repository
        // badge token, never by authentication.
        "BadgeController.Get",

        // Fork pull-request coverage (M16). Anonymous by necessity: the forge refuses to mint a
        // credential for a fork's runner, which is the whole problem D6f describes. What replaces
        // authentication is a forge round trip — the target repository must have the app installed,
        // the pull request must exist, it must genuinely be from a fork, and its head sha must
        // equal the uploaded one. Nothing it writes can move repository-level state.
        "ForkUploadsController.UploadFromFork",
    ];

    [Fact]
    public void The_anonymous_surface_is_exactly_what_is_recorded_here()
    {
        var actual = AnonymousActions()
            .Select(a => $"{a.DeclaringType!.Name}.{a.Name}")
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([.. Expected.OrderBy(n => n, StringComparer.Ordinal)], actual);
    }

    /// <summary>
    /// The test above is only worth anything if its detection actually fires, so prove it does:
    /// the app must contain at least one controller that is NOT anonymous, and the finder must not
    /// report it. A finder that silently matched nothing would make the set assertion pass
    /// vacuously forever.
    /// </summary>
    [Fact]
    public void The_finder_distinguishes_authenticated_actions()
    {
        var anonymous = AnonymousActions().Select(a => a.DeclaringType!.Name).Distinct().ToArray();
        var controllers = ControllerTypes().ToArray();

        Assert.NotEmpty(controllers);
        // UploadsController is [Authorize]d at class level and must never be in the anonymous set.
        Assert.Contains(controllers, c => c.Name == "UploadsController");
        Assert.DoesNotContain("UploadsController", anonymous);
    }

    private static IEnumerable<Type> ControllerTypes()
        => typeof(CodeCoverage.Controllers.UploadsController).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

    /// <summary>
    /// Actions reachable with no credential: either the declaring controller carries
    /// <c>[AllowAnonymous]</c>, or the action does.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>[AllowAnonymous]</c> anywhere in the chain wins over any <c>[Authorize]</c>, which is
    /// why this looks for its presence rather than for the absence of <c>[Authorize]</c>. A
    /// controller with both is anonymous, and a reader skimming for <c>[Authorize]</c> would
    /// conclude the opposite.
    /// </remarks>
    private static IEnumerable<MethodInfo> AnonymousActions()
    {
        foreach (var controller in ControllerTypes())
        {
            var controllerIsAnonymous = controller.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;

            var actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialGroupingMember());

            foreach (var action in actions)
            {
                if (controllerIsAnonymous || action.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null)
                    yield return action;
            }
        }
    }
}

file static class MethodInfoExtensions
{
    /// <summary>Property accessors, operators and object overrides are not actions.</summary>
    public static bool IsSpecialGroupingMember(this MethodInfo method)
        => method.IsSpecialName || method.DeclaringType == typeof(object);
}
