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
/// fixture, and the strict default is unchanged. That a licence-less embedded server really does boot
/// and serve store/load/query/update was measured out-of-tree during planning (see
/// <c>docs/issue_265_plan.md</c>, spike S2), and the real coverage for it is a fork CI run — which
/// genuinely has no licence, and is the exact scenario the option exists for.
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
    public void The_default_is_to_require_a_licence()
    {
        // Pins the default against an accidental flip, which would be silent: a suite would start
        // running in restricted mode and only fail once some test reached a licensed feature.
        new StrictFixture().Strictness.Should().BeTrue();
    }

    private sealed class StrictFixture : SparkTestDriver
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
