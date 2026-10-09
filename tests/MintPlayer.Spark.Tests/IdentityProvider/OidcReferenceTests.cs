using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The reference types decide which document a credential or a grant resolves to. A collision
/// here is not a bug in a lookup — it is one caller's authorization answering for another's.
/// </summary>
public class OidcReferenceTests
{
    [Fact]
    public void TokenDocumentId_IsDeterministic()
    {
        OidcTokenReference.DocumentId("abc").Should().Be(OidcTokenReference.DocumentId("abc"));
    }

    [Fact]
    public void TokenDocumentId_DiffersPerValue()
    {
        OidcTokenReference.DocumentId("abd").Should().NotBe(OidcTokenReference.DocumentId("abc"));
    }

    [Fact]
    public void TokenDocumentId_DoesNotContainTheValue()
    {
        // The point of hashing into the id: a database dump must not yield a replayable credential.
        var value = "super-secret-refresh-token";
        OidcTokenReference.DocumentId(value).Should().NotContain(value);
    }

    [Fact]
    public void TokenAndRequestIds_LandInTheTokenCollection()
    {
        // A pending authorization request is an OidcToken (Type authorization_request) now, so both
        // handles hash into OidcTokens/. They cannot collide in practice: every handle is a fresh
        // random value (GeneratedHandles_AreUnique), and the Type field tells the two apart.
        OidcTokenReference.DocumentId("x").Should().StartWith("OidcTokens/");
        OidcRequestReference.DocumentId("x").Should().StartWith("OidcTokens/");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void DocumentId_RejectsEmptyValues(string? value)
    {
        new Action(() => OidcTokenReference.DocumentId(value!)).Should().Throw<ArgumentException>();
        new Action(() => OidcRequestReference.DocumentId(value!)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GeneratedHandles_AreUnique()
    {
        var handles = Enumerable.Range(0, 1000).Select(_ => OidcRequestReference.GenerateValue()).ToList();
        handles.Distinct(StringComparer.Ordinal).Count().Should().Be(handles.Count);
    }

    [Fact]
    public void GeneratedHandles_AreUrlSafe()
    {
        var handle = OidcRequestReference.GenerateValue();
        Uri.EscapeDataString(handle).Should().Be(handle);
    }

    [Fact]
    public void AuthorizationId_IsDeterministicPerPair()
    {
        OidcGrantReference.DocumentId("SparkUsers/1", "OidcApplications/a").Should().Be(OidcGrantReference.DocumentId("SparkUsers/1", "OidcApplications/a"));
    }

    [Fact]
    public void AuthorizationId_DiffersPerSubjectAndPerApplication()
    {
        var baseline = OidcGrantReference.DocumentId("SparkUsers/1", "OidcApplications/a");

        OidcGrantReference.DocumentId("SparkUsers/2", "OidcApplications/a").Should().NotBe(baseline);
        OidcGrantReference.DocumentId("SparkUsers/1", "OidcApplications/b").Should().NotBe(baseline);
    }

    [Fact]
    public void AuthorizationId_IsNotConfusableAcrossTheSeparator()
    {
        // The two pairs concatenate to the same string under a bare separator. Length framing
        // is what keeps them apart.
        OidcGrantReference.DocumentId("x", "y|z").Should().NotBe(OidcGrantReference.DocumentId("x|y", "z"));
    }

    [Fact]
    public void AuthorizationId_DoesNotSwapSubjectAndApplication()
    {
        OidcGrantReference.DocumentId("app", "alice").Should().NotBe(OidcGrantReference.DocumentId("alice", "app"));
    }

    [Theory]
    [InlineData("", "app")]
    [InlineData("alice", "")]
    public void AuthorizationId_RejectsEmptyParts(string subject, string applicationId)
    {
        new Action(() => OidcGrantReference.DocumentId(subject, applicationId)).Should().Throw<ArgumentException>();
    }
}
