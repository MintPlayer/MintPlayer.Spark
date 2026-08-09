using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// What an unauthenticated caller can actually do to <c>/spark/po/*</c> — the framework's core CRUD
/// surface — against a real host.
/// <para>
/// This existed nowhere. Anonymous access was covered only at the <b>introspection</b> layer:
/// <c>MetadataEndpointAuthTests</c> and <c>PermissionsEndpointAuthTests</c> assert what the server
/// <i>reports</i> an anonymous caller may do, which is a different question from what it actually
/// permits. A permissions endpoint answering "CanCreate: false" while the create endpoint accepted
/// the request would satisfy every test that existed.
/// </para>
/// <para>
/// Fleet's <c>security.json</c> grants <c>Everyone</c> exactly one right — <c>QueryRead/Company</c>
/// — so these tests pin both sides of that line: Company is readable without authenticating, Car is
/// not, and nothing is writable.
/// </para>
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class AnonymousPersistentObjectAccessTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public AnonymousPersistentObjectAccessTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>No login. The framework must treat this exactly as the <c>Everyone</c> baseline.</summary>
    private SparkClient Anonymous() => SparkClientFactory.ForFleet(_fixture.Host);

    [Fact]
    public async Task Anonymous_cannot_list_Cars()
    {
        using var client = Anonymous();

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.ListPersistentObjectsAsync(CarFixture.TypeId));

        // Exactly 401, not "one of 401/403": Spark returns 403 only when the caller IS
        // authenticated and still lacks the right. Accepting either would stop distinguishing
        // "refused because anonymous" from "refused despite a session", which is the whole subject.
        ex.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "Car is granted to Administrators and Fleet managers, never to Everyone — and an "
            + "unauthenticated caller holds only Everyone's rights");
    }

    /// <summary>
    /// The other side of the line, and the one that would catch an over-correction: `Everyone` does
    /// grant <c>QueryRead/Company</c>, so this must keep working without a login. A change that
    /// locked anonymous callers out entirely would be a behaviour change in the demos, not a fix.
    /// </summary>
    [Fact]
    public async Task Anonymous_can_list_Companies()
    {
        using var client = Anonymous();

        var companies = await client.ListPersistentObjectsAsync(CompanyTypeName);

        companies.Should().NotBeNull(
            "security.json grants QueryRead/Company to Everyone, which applies to callers who "
            + "never authenticated");
    }

    [Fact]
    public async Task Anonymous_cannot_create_a_Car()
    {
        using var client = Anonymous();

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.CreatePersistentObjectAsync(
                CarFixture.New(CarFixture.RandomLicensePlate("AN"), model: "ANON")));

        // SparkClient primes and echoes the XSRF token itself, so this request clears the
        // antiforgery gate and is refused by authorization proper — which is the stronger result.
        // Accepting a 400 here would let the test pass on the antiforgery gate alone and never
        // exercise whether anonymous callers can create.
        ex.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an anonymous caller holds only Everyone's rights, which do not include creating a Car");
    }

    /// <summary>
    /// Everyone's grant is <c>QueryRead</c> — read only. Write access must not come with it, which
    /// is the distinction a single combined right would blur.
    /// </summary>
    [Fact]
    public async Task Anonymous_cannot_create_a_Company_despite_being_able_to_read_them()
    {
        using var client = Anonymous();

        var company = new PersistentObject
        {
            Name = CompanyTypeName,
            ObjectTypeId = await ResolveCompanyTypeIdAsync(client),
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Name", Value = "Anonymous Ltd" },
            ],
        };

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.CreatePersistentObjectAsync(company));

        // N23 — this asserts 400, not 401, and that is the finding rather than the intent.
        //
        // CreatePersistentObject validates the payload (Create.cs:62) BEFORE the authorization
        // check, which lives inside SavePersistentObjectAsync (:68). This payload omits attributes
        // Company requires, so it is rejected as invalid and never reaches the permission check at
        // all. The sibling Car case returns 401 only because CarFixture builds a *valid* payload.
        //
        // The refusal is not in question — an anonymous caller cannot create a Company either way.
        // What leaks is which fields an entity type requires, for a type the caller has no right
        // to create. Recorded rather than fixed here: separating "may I create this?" from "is
        // this valid?" means DatabaseAccess exposing the authorization check independently of the
        // save, which is the same chokepoint restructuring as M11.
        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "validation currently precedes authorization on the create path (N23) — when that is "
            + "reordered, this becomes 401 and this assertion should be updated to match");
    }

    private const string CompanyTypeName = "Company";

    private static async Task<Guid> ResolveCompanyTypeIdAsync(SparkClient client)
    {
        var types = await client.ListEntityTypesAsync();
        var company = types.FirstOrDefault(t =>
            string.Equals(t.Name, CompanyTypeName, StringComparison.OrdinalIgnoreCase));

        company.Should().NotBeNull(
            "Company is visible to anonymous callers, so its definition must be listable");
        return company!.Id;
    }
}
