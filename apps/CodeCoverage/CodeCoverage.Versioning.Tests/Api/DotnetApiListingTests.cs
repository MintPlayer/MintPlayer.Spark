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

    private const string Shapes = "CodeCoverage.Versioning.Tests.Api.ApiFixture.Shapes`2";

    [Theory]
    [InlineData("System.Char Letter = 'x'")]
    [InlineData("System.SByte Signed = -1")]
    [InlineData("System.Int16 Short = -2")]
    [InlineData("System.UInt16 UShort = 3")]
    [InlineData("System.UInt32 UInt = 4")]
    [InlineData("System.UInt64 ULong = 5")]
    [InlineData("System.Single Single = 1.5")]
    [InlineData("System.Double Double = 2.25")]
    [InlineData("System.String Text = \"hi\"")]
    [InlineData("System.Boolean Flag = true")]
    public void Every_constant_type_shows_its_value(string field)
        => Lines.Should().Contain($"{Shapes} public const field {field}");

    [Fact]
    public void Public_and_protected_events_are_listed_and_private_ones_are_not()
    {
        Lines.Should().Contain($"{Shapes} public event System.EventHandler Changed");
        Lines.Should().Contain($"{Shapes} protected event System.EventHandler Guarded");
        Lines.Should().NotContain(l => l.Contains("Hidden"));
    }

    [Fact]
    public void Generic_parameters_show_their_variance_and_constraints()
    {
        Lines.Should().Contain("class CodeCoverage.Versioning.Tests.Api.ApiFixture.Shapes`2<`0:class+System.IComparable`1<!0>, `1:struct+new()+System.ValueType>");
        Lines.Should().Contain("interface CodeCoverage.Versioning.Tests.Api.ApiFixture.IVariant`2<`0:out, `1:in>");
        Lines.Should().Contain("CodeCoverage.Versioning.Tests.Api.ApiFixture.IVariant`2 public abstract method !0 Run(!1)");
        Lines.Should().Contain($"{Shapes} public method !!0 Generic<`0:new()>(!!0)");
        Lines.Should().Contain($"{Shapes} public method !0 Echo(!0, !1)");
    }

    [Theory]
    [InlineData("System.Int32[,] Grid(System.Int32[,])")]
    [InlineData("System.IntPtr Native(System.UIntPtr)")]
    [InlineData("System.Void Read(System.Int32&, System.Int32&, out System.Int32&)")]
    [InlineData("System.Environment+SpecialFolder Folder(System.Environment+SpecialFolder)")]
    [InlineData("System.Object Nothing(System.Object = null)")]
    public void Signatures_name_arrays_native_ints_by_ref_parameters_and_nested_framework_types(string method)
        => Lines.Should().Contain($"{Shapes} public method {method}");

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
