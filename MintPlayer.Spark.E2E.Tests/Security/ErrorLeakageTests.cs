using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-6 — error responses must not leak implementation internals. Stack traces, RavenDB
/// index/collection names, .NET type names, or raw exception messages let an attacker
/// map the server's internals.
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

    [Fact]
    public async Task Malformed_entityTypeId_does_not_leak_stack_trace_or_internal_types()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.GetAsync(
            $"{_fixture.Host.FleetUrl}/spark/po/this-is-not-a-real-type/some-id");

        var body = await response.TextAsync();

        foreach (var token in LeakyTokens)
            body.Should().NotContain(token, $"error response leaked internal token '{token}'");
    }

    [Fact]
    public async Task Malformed_id_on_get_does_not_leak_internals()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Valid type, impossible ID shape.
        var response = await page.APIRequest.GetAsync(
            $"{_fixture.Host.FleetUrl}/spark/po/Car/{{}}");

        var body = await response.TextAsync();
        foreach (var token in LeakyTokens)
            body.Should().NotContain(token, $"error response leaked internal token '{token}'");
    }

    [Fact]
    public async Task Bad_lookup_reference_key_does_not_leak_internals()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Login so the handler reaches the service layer (where exceptions originate).
        var login = await page.APIRequest.PostAsync($"{_fixture.Host.FleetUrl}/spark/auth/login?useCookies=true", new()
        {
            DataObject = new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass },
        });
        login.Status.Should().Be(200);

        var response = await page.APIRequest.DeleteAsync(
            $"{_fixture.Host.FleetUrl}/spark/lookupref/NoSuchCollection/NoSuchKey");

        var body = await response.TextAsync();
        foreach (var token in LeakyTokens)
            body.Should().NotContain(token, $"lookup-delete error leaked internal token '{token}'");
    }
}
