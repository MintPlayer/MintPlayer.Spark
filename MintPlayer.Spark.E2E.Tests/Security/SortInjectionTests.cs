using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-5 — the <c>sortColumns</c> query-string parameter must be validated against the
/// query's declared attribute schema. Accepting an arbitrary property name via reflection
/// lets a caller sort on fields the developer never intended to expose (leaking ordering
/// as a side channel, and in some index configurations materialising the value).
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class SortInjectionTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public SortInjectionTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private const string CarsQueryId = "a20e8400-e29b-41d4-a716-446655440001";

    private async Task<Microsoft.Playwright.IAPIRequestContext> LoggedInAsAdminAsync(Microsoft.Playwright.IPage page)
    {
        var login = await page.APIRequest.PostAsync($"{_fixture.Host.FleetUrl}/spark/auth/login?useCookies=true", new()
        {
            DataObject = new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass },
        });
        login.Status.Should().Be(200, $"admin login failed: {await login.TextAsync()}");
        return page.APIRequest;
    }

    [Fact]
    public async Task Sort_by_nonexistent_property_is_rejected()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        var api = await LoggedInAsAdminAsync(page);

        var response = await api.GetAsync(
            $"{_fixture.Host.FleetUrl}/spark/queries/{CarsQueryId}/execute?sortColumns=NoSuchProperty:asc");

        response.Status.Should().BeOneOf(new[] { 400, 422 },
            "sorting by a property not in the query's schema must be rejected, not silently ignored");
    }

    [Fact]
    public async Task Sort_by_metadata_property_on_projection_is_rejected()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        var api = await LoggedInAsAdminAsync(page);

        // The VCar projection is a C# class; any public property on it is reflectable.
        // An attribute like "Metadata" or any non-declared public property is not in the
        // JSON schema for the query — the framework must not accept it.
        var response = await api.GetAsync(
            $"{_fixture.Host.FleetUrl}/spark/queries/{CarsQueryId}/execute?sortColumns=Id:asc");

        response.Status.Should().BeOneOf(new[] { 400, 422 },
            "sorting by a reflectable-but-undeclared property (Id) must be rejected");
    }

    [Fact]
    public async Task Sort_with_malformed_direction_does_not_500()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        var api = await LoggedInAsAdminAsync(page);

        var response = await api.GetAsync(
            $"{_fixture.Host.FleetUrl}/spark/queries/{CarsQueryId}/execute?sortColumns=LicensePlate:not-a-direction");

        response.Status.Should().BeLessThan(500,
            "malformed direction should produce a 4xx client error, not a 500 server fault");
    }
}
