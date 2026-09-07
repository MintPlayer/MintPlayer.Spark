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
/// Fleet's <c>security.json</c> grants <c>anonymous</c> exactly one right — <c>QueryRead/Company</c>
/// — so these tests pin both sides of that line: Company is readable without authenticating, Car is
/// not, and nothing is writable.
/// </para>
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class AnonymousPersistentObjectAccessTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public AnonymousPersistentObjectAccessTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>No login. The framework must treat this exactly as the <c>anonymous</c> baseline.</summary>
    private SparkClient Anonymous() => SparkClientFactory.ForFleet(_fixture.Host);

    [Fact]
    public async Task Anonymous_cannot_list_Cars()
    {
        using var client = Anonymous();

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.ExecuteQueryAsync(GetCarsQueryId));

        // 404, and deliberately not 401. This used to read through GET /spark/po/{type}, which
        // refuses with 401 for an anonymous caller so a client knows authenticating would help. That
        // endpoint is gone, and the query path answers a DIFFERENT and equally deliberate way: a
        // denied query is byte-identical to a query that does not exist, so no caller can use
        // refusals to enumerate which queries an application has.
        //
        // The 401-versus-403 distinction that mattered here — "refused because anonymous" against
        // "refused despite a session" — is still pinned, by Anonymous_cannot_create_a_Car below,
        // which goes through a PO endpoint that still exists and still draws it.
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "Car is granted to Administrators and Fleet managers, never to anonymous callers — and on "
            + "the query path a refusal must be indistinguishable from an unknown query");
    }

    /// <summary>
    /// The other side of the line, and the one that would catch an over-correction: the anonymous group does
    /// grant <c>QueryRead/Company</c>, so this must keep working without a login. A change that
    /// locked anonymous callers out entirely would be a behaviour change in the demos, not a fix.
    /// </summary>
    [Fact]
    public async Task Anonymous_can_list_Companies()
    {
        using var client = Anonymous();

        var companies = await client.ExecuteQueryAsync(GetCompaniesQueryId);

        companies.Should().NotBeNull(
            "security.json grants QueryRead/Company to the anonymous group, which applies to callers who "
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
            "an anonymous caller holds only the anonymous group's rights, which do not include creating a Car");
    }

    /// <summary>
    /// the anonymous group's grant is <c>QueryRead</c> — read only. Write access must not come with it, which
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

        // N23, fixed in M11.4. This payload deliberately omits attributes Company requires, so
        // before the reorder it came back 400 with those validation errors — telling a caller who
        // may not create a Company exactly which fields one needs. Authorization is now asked
        // first, so the answer is the refusal and nothing else.
        ex.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an invalid payload must not reveal an entity type's validation rules to a caller with "
            + "no right to create it — the refusal comes first");
    }

    private const string CompanyTypeName = "Company";

    // The listing endpoint these tests used (GET /spark/po/{type}) is gone — a second, uncapped list
    // pipeline — so they exercise the surviving one. Not a like-for-like swap to note: the old call
    // asserted "anonymous is refused" through a path that no longer exists, and after its deletion
    // the same call answered 401 anyway, from the catch-all detail route with an empty id. It would
    // have kept passing while testing nothing.
    private static readonly Guid GetCarsQueryId = Guid.Parse("a20e8400-e29b-41d4-a716-446655440001");
    private static readonly Guid GetCompaniesQueryId = Guid.Parse("a20e8400-e29b-41d4-a716-446655440003");

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
