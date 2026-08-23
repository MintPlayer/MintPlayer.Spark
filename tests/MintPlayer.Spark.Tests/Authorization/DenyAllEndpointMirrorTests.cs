using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.Abstractions;

using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// Owns the deny-all host for the whole class: one database, one Spark host, one minted
/// antiforgery token, instead of one of each per test case.
/// </summary>
/// <remarks>
/// Nothing in this class writes a document — every case asks an endpoint what it refuses — so a
/// shared database costs it nothing and saves 26 create/delete cycles plus 26 host boots.
/// </remarks>
public sealed class DenyAllHost : SparkSharedDatabase
{
    internal static readonly Guid DocTypeId = Guid.Parse("5a5a0000-1111-2222-3333-444455556666");
    internal static readonly Guid AllDocsQueryId = Guid.Parse("5a5a1111-1111-2222-3333-444455556666");

    public SparkEndpointFactory<GuardedContext> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// Antiforgery runs BEFORE authorization, so an unminted mutating request answers 400 and
    /// proves nothing about the permission check. Minted once for the class.
    /// </summary>
    public string CookieHeader { get; private set; } = null!;

    /// <inheritdoc cref="CookieHeader"/>
    public string XsrfToken { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var docType = GuardedDocModel.For(DocTypeId);
        docType.Queries =
        [
            new SparkQuery { Id = AllDocsQueryId, Name = "AllDocs", Source = "Database.Docs", EntityType = "GuardedDoc" },
        ];

        Factory = new SparkEndpointFactory<GuardedContext>(
            Store, [docType], security: SparkTestSecurity.Empty);
        Client = Factory.CreateClient();

        (CookieHeader, XsrfToken) = await Factory.MintAntiforgeryAsync();
    }

    public override async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
            await Factory.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Every Spark endpoint, against a <c>security.json</c> that grants nothing.
/// </summary>
/// <remarks>
/// This is the suite that turns a deleted permission check into a red build. Nothing else does:
/// the other 240-odd factory-booted tests run under a permissive baseline, so removing
/// <c>EnsureAuthorizedAsync</c> from <c>DatabaseAccess</c> leaves every one of them passing.
/// <para>
/// Table-driven over one host on purpose. The value is in the <em>list</em> — an endpoint absent
/// from it is an endpoint nobody proved consults the permission service — so adding a row has to
/// cost one line.
/// </para>
/// <para>
/// Spark's endpoints fall into two kinds and the split is the interesting part:
/// </para>
/// <list type="bullet">
/// <item><b>Access endpoints</b> serve or mutate data and answer 404, byte-identically to a
/// genuine not-found (audit M-3). 404 rather than 401 here because this host registers no
/// credential scheme, so authenticating could not help — see
/// <see cref="Endpoints.SparkDenialPredicateTests"/> for the other branch.</item>
/// <item><b>Catalogue endpoints</b> are loaded by the client shell on boot for every visitor, so
/// refusing would bounce an anonymous visitor to sign-in merely for opening a page. They answer
/// 200 with everything filtered out. "200" alone would also be true of a leak, so each assertion
/// names what must not appear in the body.</item>
/// </list>
/// </remarks>
public class DenyAllEndpointMirrorTests(DenyAllHost host)
    : SparkSharedTestDriver(host), IClassFixture<DenyAllHost>
{
    private static readonly Guid DocTypeId = DenyAllHost.DocTypeId;
    private static readonly Guid AllDocsQueryId = DenyAllHost.AllDocsQueryId;

    private HttpClient _client => host.Client;
    private string _cookieHeader => host.CookieHeader;
    private string _xsrfToken => host.XsrfToken;

    public static TheoryData<string, string> AccessEndpoints => new()
    {
        { "GET", $"/spark/po/{DocTypeId}" },
        { "GET", $"/spark/po/{DocTypeId}/docs%2F1" },
        { "POST", $"/spark/po/{DocTypeId}" },
        { "PUT", $"/spark/po/{DocTypeId}/docs%2F1" },
        { "DELETE", $"/spark/po/{DocTypeId}/docs%2F1" },
        { "GET", $"/spark/queries/{AllDocsQueryId}" },
        { "GET", $"/spark/queries/{AllDocsQueryId}/execute" },
        { "POST", $"/spark/actions/{DocTypeId}/Archive" },
        { "GET", "/spark/lookupref/Colour" },
    };

    [Theory]
    [MemberData(nameof(AccessEndpoints))]
    public async Task An_access_endpoint_refuses_when_nothing_is_granted(string method, string path)
    {
        var (status, _) = await SendAsync(method, path);

        status.Should().Be(
            HttpStatusCode.NotFound,
            $"{method} {path} must not serve a caller who holds no right");
    }

    /// <summary>
    /// The property M-3 is actually about, and the one a fixed-message assertion would miss: a
    /// refusal must be indistinguishable from a genuine not-found. Equal status is not enough —
    /// a differing body is the same oracle one field lower.
    /// </summary>
    /// <remarks>
    /// Each row is sent twice: once against the fixture's real id, once against an id that exists
    /// nowhere. Comparing the two responses rather than either against a literal is what makes the
    /// assertion survive a change of wording — and what catches an endpoint that starts
    /// interpolating something it should not.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AccessEndpoints))]
    public async Task A_refusal_is_byte_identical_to_a_genuine_not_found(string method, string path)
    {
        var absent = Guid.NewGuid();
        var unknownPath = path
            .Replace(DocTypeId.ToString(), absent.ToString())
            .Replace(AllDocsQueryId.ToString(), absent.ToString())
            .Replace("Colour", "NoSuchLookup");

        var (deniedStatus, deniedBody) = await SendAsync(method, path);
        var (unknownStatus, unknownBody) = await SendAsync(method, unknownPath);

        unknownStatus.Should().Be(deniedStatus, $"{method} {path}");
        Normalize(unknownBody).Should().Be(Normalize(deniedBody), $"{method} {path}");

        // The caller's own input, echoed back. `Queries/Get` says "Query '{id}' not found" for both
        // branches, which is byte-identical FOR ONE REQUEST — the property M-3 needs — but differs
        // between the two requests this test makes, because the id differs. Blanking the
        // caller-supplied identifiers is what leaves only the part the server chose.
        string Normalize(string body) => body
            .Replace(absent.ToString(), "<id>")
            .Replace(DocTypeId.ToString(), "<id>")
            .Replace(AllDocsQueryId.ToString(), "<id>")
            .Replace("NoSuchLookup", "<id>")
            .Replace("Colour", "<id>");
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);

        if (method is "POST" or "PUT")
            request.Content = JsonContent.Create(new { });

        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/spark/types")]
    [InlineData("/spark/queries")]
    [InlineData("/spark/aliases")]
    [InlineData("/spark/program-units")]
    public async Task A_catalogue_endpoint_answers_an_empty_catalogue(string path)
    {
        using var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("GuardedDoc", $"{path} must not name a type the caller may not query");
        body.Should().NotContain("AllDocs", $"{path} must not name a query the caller may not run");
    }

    /// <summary>
    /// The per-type action listing is a catalogue endpoint too — the shell asks it for every type
    /// it renders. It must answer the empty list for a denied type AND for a type that does not
    /// exist, because the difference between those two answers is the whole of M-3.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_action_listing_is_empty_for_a_denied_type_and_an_unknown_one(bool known)
    {
        var id = known ? DocTypeId.ToString() : Guid.NewGuid().ToString();

        using var response = await _client.GetAsync($"/spark/actions/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("[]");
    }

    /// <summary>
    /// <c>/spark/permissions</c> is anonymous-callable by design (audit M-1) and closes its half of
    /// the oracle the other way: every right false, for a denied type and an unknown one alike.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_permissions_endpoint_reports_every_right_false(bool known)
    {
        var id = known ? DocTypeId.ToString() : Guid.NewGuid().ToString();

        using var response = await _client.GetAsync($"/spark/permissions/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(
            "true", "no right is granted, so nothing may be reported as allowed");
    }
}
