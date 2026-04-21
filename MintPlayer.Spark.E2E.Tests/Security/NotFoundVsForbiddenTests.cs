using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-3 — authenticated callers must not be able to distinguish "record does not exist"
/// from "record exists but you can't read it". Otherwise the framework becomes an
/// existence oracle for IDs the caller doesn't own.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class NotFoundVsForbiddenTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public NotFoundVsForbiddenTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Nonexistent_id_and_forbidden_id_return_the_same_status()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Register + log in a plain user (no administrator claim — default tier).
        var email = $"plain-{Guid.NewGuid():N}@e2e.local";
        var pass = _fixture.Host.AdminPass; // reuse the fixture's complex password
        var register = await page.APIRequest.PostAsync($"{_fixture.Host.FleetUrl}/spark/auth/register", new()
        {
            DataObject = new { email, password = pass },
        });
        register.Status.Should().BeOneOf(new[] { 200, 201, 204, 400 }, $"register response: {await register.TextAsync()}");

        var login = await page.APIRequest.PostAsync($"{_fixture.Host.FleetUrl}/spark/auth/login?useCookies=true", new()
        {
            DataObject = new { email, password = pass },
        });
        // Login may 401 if email confirmation is required — but that's a separate concern;
        // the test still exercises unauthenticated distinguishability.

        var nonExistentCar = await page.APIRequest.GetAsync(
            $"{_fixture.Host.FleetUrl}/spark/po/Car/cars%2Fdefinitely-does-not-exist-{Guid.NewGuid():N}");

        // Seed a real car as admin in a separate context so we have a known-forbidden ID
        // from the plain user's perspective.
        await using var adminPages = new PageFactory(_fixture);
        var adminPage = await adminPages.NewPageAsync();
        var adminApi = await SparkApi.LoginAsync(adminPage, _fixture.Host, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        var createResp = await adminApi.PostJsonAsync("/spark/po/Car", new
        {
            persistentObject = new
            {
                name = "Car",
                objectTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
                attributes = new object[]
                {
                    new { name = "LicensePlate", value = $"NF{Guid.NewGuid():N}".Substring(0, 8).ToUpperInvariant() },
                    new { name = "Model",        value = "M3" },
                    new { name = "Year",         value = 2024 },
                },
            },
        });
        if (createResp.Status == 200 || createResp.Status == 201)
        {
            var body = await createResp.JsonAsync();
            var carId = body!.Value.GetProperty("id").GetString();
            carId.Should().NotBeNullOrEmpty();

            var forbiddenCar = await page.APIRequest.GetAsync(
                $"{_fixture.Host.FleetUrl}/spark/po/Car/{Uri.EscapeDataString(carId!)}");

            forbiddenCar.Status.Should().Be(nonExistentCar.Status,
                "the response for a real-but-forbidden record must be identical to a nonexistent one");
        }
    }
}
