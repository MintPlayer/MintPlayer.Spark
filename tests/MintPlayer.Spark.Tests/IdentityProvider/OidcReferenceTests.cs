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
        Assert.Equal(OidcTokenReference.DocumentId("abc"), OidcTokenReference.DocumentId("abc"));
    }

    [Fact]
    public void TokenDocumentId_DiffersPerValue()
    {
        Assert.NotEqual(OidcTokenReference.DocumentId("abc"), OidcTokenReference.DocumentId("abd"));
    }

    [Fact]
    public void TokenDocumentId_DoesNotContainTheValue()
    {
        // The point of hashing into the id: a database dump must not yield a replayable credential.
        var value = "super-secret-refresh-token";
        Assert.DoesNotContain(value, OidcTokenReference.DocumentId(value), StringComparison.Ordinal);
    }

    [Fact]
    public void TokenAndRequestIds_LandInDifferentCollections()
    {
        Assert.StartsWith("OidcTokens/", OidcTokenReference.DocumentId("x"), StringComparison.Ordinal);
        Assert.StartsWith("OidcAuthorizationRequests/", OidcRequestReference.DocumentId("x"), StringComparison.Ordinal);
    }

    [Fact]
    public void SameHandle_InDifferentCollections_DoesNotCollide()
    {
        Assert.NotEqual(OidcTokenReference.DocumentId("x"), OidcRequestReference.DocumentId("x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void DocumentId_RejectsEmptyValues(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => OidcTokenReference.DocumentId(value!));
        Assert.ThrowsAny<ArgumentException>(() => OidcRequestReference.DocumentId(value!));
    }

    [Fact]
    public void GeneratedHandles_AreUnique()
    {
        var handles = Enumerable.Range(0, 1000).Select(_ => OidcRequestReference.GenerateValue()).ToList();
        Assert.Equal(handles.Count, handles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GeneratedHandles_AreUrlSafe()
    {
        var handle = OidcRequestReference.GenerateValue();
        Assert.Equal(handle, Uri.EscapeDataString(handle));
    }

    [Fact]
    public void AuthorizationId_IsDeterministicPerPair()
    {
        Assert.Equal(
            OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/a"),
            OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/a"));
    }

    [Fact]
    public void AuthorizationId_DiffersPerSubjectAndPerApplication()
    {
        var baseline = OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/a");

        Assert.NotEqual(baseline, OidcAuthorizationReference.DocumentId("SparkUsers/2", "OidcApplications/a"));
        Assert.NotEqual(baseline, OidcAuthorizationReference.DocumentId("SparkUsers/1", "OidcApplications/b"));
    }

    [Fact]
    public void AuthorizationId_IsNotConfusableAcrossTheSeparator()
    {
        // The two pairs concatenate to the same string under a bare separator. Length framing
        // is what keeps them apart.
        Assert.NotEqual(
            OidcAuthorizationReference.DocumentId("x|y", "z"),
            OidcAuthorizationReference.DocumentId("x", "y|z"));
    }

    [Fact]
    public void AuthorizationId_DoesNotSwapSubjectAndApplication()
    {
        Assert.NotEqual(
            OidcAuthorizationReference.DocumentId("alice", "app"),
            OidcAuthorizationReference.DocumentId("app", "alice"));
    }

    [Theory]
    [InlineData("", "app")]
    [InlineData("alice", "")]
    public void AuthorizationId_RejectsEmptyParts(string subject, string applicationId)
    {
        Assert.ThrowsAny<ArgumentException>(() => OidcAuthorizationReference.DocumentId(subject, applicationId));
    }
}
