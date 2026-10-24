using CodeCoverage.Versioning.Api;

namespace CodeCoverage.Versioning.Tests.Api;

public class DotnetApiListingTests
{
    // The fixture types live in this test assembly; the lister reads the assembly file, never loads it again.
    static readonly IReadOnlyList<string> Lines = ListFixture();

    static IReadOnlyList<string> ListFixture()
    {
        using var stream = File.OpenRead(typeof(DotnetApiListingTests).Assembly.Location);
        return DotnetApiListing.List(stream).Where(l => l.Contains("ApiFixture.")).ToList();
    }

    [Fact]
    public void Public_and_protected_members_are_listed()
    {
        Lines.Should().Contain("CodeCoverage.Versioning.Tests.Api.ApiFixture.Widget public method System.String Render(System.Int32 = 2)");
        Lines.Should().Contain("CodeCoverage.Versioning.Tests.Api.ApiFixture.Widget protected virtual method System.Void OnRender()");
        Lines.Should().Contain("CodeCoverage.Versioning.Tests.Api.ApiFixture.Widget property System.Int32 Size { public get; protected set }");
    }

    [Fact]
    public void Private_and_internal_members_are_not()
    {
        Lines.Should().NotContain(l => l.Contains("Secret") || l.Contains("Internals"));
    }

    [Fact]
    public void A_sealed_types_protected_override_is_not_listed()
    {
        Lines.Should().NotContain(l => l.Contains("SealedWidget") && l.Contains("OnRender"));
    }

    [Fact]
    public void An_enum_lists_its_underlying_type_and_values()
    {
        Lines.Should().Contain("enum CodeCoverage.Versioning.Tests.Api.ApiFixture.Size : System.Byte");
        Lines.Should().Contain("CodeCoverage.Versioning.Tests.Api.ApiFixture.Size public const field CodeCoverage.Versioning.Tests.Api.ApiFixture.Size Large = 2");
    }

    [Fact]
    public void Lines_are_sorted_ordinally_and_the_hash_is_stable()
    {
        Lines.Should().BeInAscendingOrder(StringComparer.Ordinal);
        DotnetApiListing.Hash(Lines).Should().Be(DotnetApiListing.Hash(ListFixture()));
        DotnetApiListing.Hash(Lines).Should().MatchRegex("^sha256:[0-9a-f]{64}$");
    }

    [Fact]
    public void A_public_change_changes_the_hash()
    {
        DotnetApiListing.Hash([.. Lines, "extra"]).Should().NotBe(DotnetApiListing.Hash(Lines));
    }
}
