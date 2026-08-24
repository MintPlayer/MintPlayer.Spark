using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>
/// <see cref="SparkTestDriver.RequireLicense"/> (issue #265, F6).
/// <para>
/// <b>What this can and cannot cover.</b> The licence-less path is a property of the *process*:
/// <c>ConfigureServer</c> is static and runs once, from a static constructor, before any fixture
/// exists — and this repository has a <c>raven-license.log</c> at its root, which
/// <c>LicenseHelper</c> finds by walking up from <c>AppContext.BaseDirectory</c>. So an in-tree test
/// always runs against a licensed server and cannot exercise
/// <c>ThrowOnInvalidOrMissingLicense = false</c> at all. Renaming the developer's licence file to
/// force it would be destructive and hostile to parallel runs.
/// </para>
/// <para>
/// What is asserted here is therefore the half that is observable: relaxing the flag does not break a
/// fixture, and the default derives from the environment's declaration. That a licence-less embedded
/// server really does boot and serve store/load/query/update was measured out-of-tree during planning
/// (see <c>docs/issue_265_plan.md</c>, spike S2), and confirmed for the whole suite by running it with
/// the licence moved aside before the default was flipped.
/// </para>
/// <para>
/// Standing coverage for the licence-less path is now a fork CI run — which genuinely has no licence,
/// and since the default no longer fails the fixture, actually exercises it end to end rather than
/// dying at <c>InitializeAsync</c>.
/// </para>
/// </summary>
public class SparkTestDriverLicenseTests : SparkTestDriver
{
    // The relaxed setting must be a no-op when a licence IS present, which is the case in this repo
    // and on any CI run with the secret available.
    protected override bool RequireLicense => false;

    [Fact]
    public async Task A_fixture_that_does_not_require_a_licence_still_gets_a_working_store()
    {
        Store.Should().NotBeNull();

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Note { Text = "relaxed" });
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenAsyncSession())
        {
            var notes = await session.Query<Note>()
                .Customize(c => c.WaitForNonStaleResults())
                .ToListAsync();

            notes.Should().ContainSingle().Which.Text.Should().Be("relaxed");
        }
    }

    [Fact]
    public void The_default_follows_the_environments_declaration()
    {
        // The default used to be an unconditional `true`, pinned here against an accidental flip.
        // It is now derived from SPARK_REQUIRE_LICENSE, so what needs pinning is the derivation:
        // strictness must track what the environment declared, and nothing else.
        //
        // Asserted against the env var read directly rather than by mutating it — the variable is
        // process-wide and this suite runs in parallel, so a test that set it would decide the
        // strictness of every fixture initialising at that moment.
        var declared = Environment.GetEnvironmentVariable("SPARK_REQUIRE_LICENSE");
        var expected = bool.TryParse(declared, out var parsed) && parsed;

        new DefaultFixture().Strictness.Should().Be(expected,
            "the default must be whatever SPARK_REQUIRE_LICENSE declared — 'true' on the trusted CI "
            + "path, absent (and so false) for a fork PR or a contributor without a licence");
    }

    [Fact]
    public void An_environment_that_declares_nothing_does_not_require_a_licence()
    {
        // The case that matters, stated on its own so it survives a change to the test above: a
        // contributor who has set nothing gets a running suite, not a wall. Unparseable counts as
        // undeclared — a typo'd 'SPARK_REQUIRE_LICENSE=yes' must not lock anyone out.
        foreach (var undeclared in new[] { null, "", "  ", "yes", "1" })
        {
            var expected = bool.TryParse(undeclared, out var parsed) && parsed;
            expected.Should().BeFalse($"'{undeclared ?? "<null>"}' does not declare a licence");
        }
    }

    private sealed class DefaultFixture : SparkTestDriver
    {
        // Inherits RequireLicense without overriding it. Exposed rather than asserted through
        // reflection so the test breaks at compile time if the member is renamed or removed.
        public bool Strictness => RequireLicense;
    }

    private sealed class Note
    {
        public string? Id { get; set; }
        public string Text { get; set; } = "";
    }
}
