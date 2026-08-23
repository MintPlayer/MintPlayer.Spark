using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-6 — error responses must not leak implementation internals. Stack traces, RavenDB
/// index/collection names, .NET type names, or raw exception messages let an attacker
/// map the server's internals. Uses <see cref="SparkClient"/>: failures surface as
/// <see cref="SparkClientException"/> and the caller inspects <c>ResponseBody</c> for
/// the same token-matching the original test performed on raw response text.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ErrorLeakageTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ErrorLeakageTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private static readonly string[] LeakyTokens = new[]
    {
        "at MintPlayer.",     // stack frame
        "at Raven.",          // Raven stack frame
        "at Microsoft.",      // framework stack frame
        "System.NullReferenceException",
        "System.InvalidOperationException",
        "ArgumentException:",
        "Raven.Client.Exceptions",
        "IndexDoesNotExistException",
        "NonUniqueObjectException",
    };

    private static void AssertNoLeakyTokens(string? body, string context)
    {
        body.Should().NotBeNull($"error response for {context} should carry a body the server wrote");
        foreach (var token in LeakyTokens)
            body!.Should().NotContain(token, $"{context} leaked internal token '{token}'");
    }

    [Fact]
    public async Task Malformed_entityTypeId_does_not_leak_stack_trace_or_internal_types()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        // This client is ANONYMOUS, and Fleet can authenticate — so an access endpoint answers
        // 401, not 404. That is deliberate (#310): the status is a function of the CALLER, never
        // of whether the resource exists, and 401 is what lets the client redirect to sign-in.
        // A host with no way to sign in answers 404 to everyone instead; both are pinned by
        // SparkDenialPredicateTests.
        //
        // The subject here is leakage, not the status: whichever refusal shape applies, the body
        // must carry nothing internal. A bogus type name and a bogus query id exercise the two
        // paths that could interpolate something they should not.
        var unknownType = await Assert.ThrowsAsync<SparkClientException>(
            () => client.GetPersistentObjectAsync("this-is-not-a-real-type", "some-id"));
        AssertNoLeakyTokens(unknownType.ResponseBody, "Get with unknown entity type");

        var unknownQuery = await Assert.ThrowsAsync<SparkClientException>(
            () => client.ExecuteQueryAsync(Guid.NewGuid()));
        AssertNoLeakyTokens(unknownQuery.ResponseBody, "ExecuteQuery on unknown id");
    }

    [Fact]
    public async Task Malformed_id_on_get_does_not_leak_internals()
    {
        using var client = SparkClientFactory.ForFleet(_fixture.Host);

        // Unknown id → client returns null (404), no body surfaced to caller. Exercise via
        // the update path which DOES throw on 404 and preserves ResponseBody.
        var carTypeId = Guid.Parse("facb6829-f2a1-4ae2-a046-6ba506e8c0ce");
        var ex = await Assert.ThrowsAsync<SparkClientException>(() => client.DeletePersistentObjectAsync(carTypeId, "{}"));
        AssertNoLeakyTokens(ex.ResponseBody, "Delete with malformed id");
    }

    [Fact]
    public async Task Bad_lookup_reference_key_does_not_leak_internals()
    {
        // /spark/lookupref/* is not yet surfaced by SparkClient — fall back to an authenticated
        // raw request via Playwright. When SparkClient grows a LookupReference method, migrate.
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var login = await page.APIRequest.PostAsync($"{_fixture.Host.FleetUrl}/spark/auth/login?useCookies=true", new()
        {
            DataObject = new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass },
        });
        login.Status.Should().Be(200);

        var response = await page.APIRequest.DeleteAsync(
            $"{_fixture.Host.FleetUrl}/spark/lookupref/NoSuchCollection/NoSuchKey");

        var body = await response.TextAsync();
        AssertNoLeakyTokens(body, "lookup-delete");
    }
}
