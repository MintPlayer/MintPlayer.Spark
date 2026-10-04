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
    public void TokenAndRequestIds_LandInDifferentCollections()
    {
        OidcTokenReference.DocumentId("x").Should().StartWith("OidcTokens/");
        OidcRequestReference.DocumentId("x").Should().StartWith("OidcAuthorizationRequests/");
    }

    [Fact]
    public void SameHandle_InDifferentCollections_DoesNotCollide()
    {
        OidcRequestReference.DocumentId("x").Should().NotBe(OidcTokenReference.DocumentId("x"));
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
        OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/a").Should().Be(OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/a"));
    }

    [Fact]
    public void AuthorizationId_DiffersPerSubjectAndPerApplication()
    {
        var baseline = OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/a");

        OidcAuthorizationReference.DocumentId("SparkUsers/2", "OidcApplications/a").Should().NotBe(baseline);
        OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/b").Should().NotBe(baseline);
    }

    [Fact]
    public void AuthorizationId_IsNotConfusableAcrossTheSeparator()
    {
        // The two pairs concatenate to the same string under a bare separator. Length framing
        // is what keeps them apart.
        OidcAuthorizationReference.DocumentId("x", "y|z").Should().NotBe(OidcAuthorizationReference.DocumentId("x|y", "z"));
    }

    [Fact]
    public void AuthorizationId_DoesNotSwapSubjectAndApplication()
    {
        OidcAuthorizationReference.DocumentId("app", "alice").Should().NotBe(OidcAuthorizationReference.DocumentId("alice", "app"));
    }

    [Theory]
    [InlineData("", "app")]
    [InlineData("alice", "")]
    public void AuthorizationId_RejectsEmptyParts(string subject, string applicationId)
    {
        new Action(() => OidcAuthorizationReference.DocumentId(subject, applicationId)).Should().Throw<ArgumentException>();
    }
}
