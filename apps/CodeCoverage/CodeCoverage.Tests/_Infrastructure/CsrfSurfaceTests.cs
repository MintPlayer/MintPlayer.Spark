using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// The exact set of state-changing controller actions that may be called <em>without</em> an
/// antiforgery token.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This exists because the app's antiforgery configuration protects nothing.</b>
/// <c>Program.cs</c> sets <c>PathPrefixes</c> and <c>WarnOnly</c> but never
/// <c>RequireAntiforgery</c>, and <c>WarnOnly</c> is documented as ignored while that is off. So the
/// path-prefix gate checks no endpoint and logs no warning — a cookie-authenticated POST with no
/// explicit metadata is checked by nothing at all. Every protected action here is protected because
/// it says so itself.
/// </para>
/// <para>
/// ⚠️ <b>Only <c>RequireAntiforgeryToken</c> counts.</b> MVC's own
/// <c>[ValidateAntiForgeryToken]</c> implements a different interface than the one Spark's
/// middleware reads, so it compiles, reads as protection, and does nothing (#300). This test looks
/// for the attribute that actually works, which is the whole reason it can catch a mistake a
/// reviewer would read straight past.
/// </para>
/// <para>
/// <b>The assertion is an exact set, not a bound</b> — the same shape as
/// <see cref="AnonymousSurfaceTests"/> and for the same reason: only equality fails when an
/// unprotected action is <em>added</em>, and failing on an addition is the point.
/// </para>
/// <para>
/// ⚠️ <b>If this test fails, do not simply add the entry.</b> It failing means a state-changing
/// endpoint became forgeable from any website a signed-in user visits. Adding the line is the last
/// step, after deciding the exemption is genuinely correct, and the comment beside it is where the
/// reason goes.
/// </para>
/// </remarks>
public class CsrfSurfaceTests
{
    /// <summary>
    /// Mutating actions deliberately exempt from antiforgery, as <c>Controller.Action</c>.
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

    [Fact]
    public void Every_state_changing_action_requires_an_antiforgery_token()
    {
        var actual = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(IsStateChanging)
            .Where(m => m.GetCustomAttribute<RequireAntiforgeryTokenAttribute>() is null
                     && m.DeclaringType!.GetCustomAttribute<RequireAntiforgeryTokenAttribute>() is null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Exact equality: a state-changing action without RequireAntiforgeryToken is forgeable from
        // any site a signed-in user visits, and the app's path-prefix gate does not cover it because
        // RequireAntiforgery is off.
        Assert.Equal([.. ExpectedExempt.OrderBy(n => n, StringComparer.Ordinal)], actual);
    }

    /// <summary>
    /// ⚠️ Guards the guard. If MVC's attribute were used by mistake it would satisfy a reviewer and
    /// not the middleware, and the test above would report the action as exempt without saying why.
    /// </summary>
    [Fact]
    public void No_controller_uses_MVCs_ineffective_antiforgery_attribute()
    {
        var misannotated = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name is "ValidateAntiForgeryTokenAttribute" or "AutoValidateAntiforgeryTokenAttribute"))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToArray();

        // MVC's antiforgery attributes implement a different interface than Spark's middleware reads,
        // so they look like protection and provide none — use RequireAntiforgeryToken instead.
        Assert.Empty(misannotated);
    }

    private static bool IsStateChanging(MethodInfo method) =>
        method.GetCustomAttributes().Any(a => a is HttpPostAttribute or HttpPutAttribute or HttpPatchAttribute or HttpDeleteAttribute);
}
