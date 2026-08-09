using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// A client certificate used as an ordinary Spark credential, end to end against a real host.
/// <para>
/// M9 made extra credential schemes reachable (before it, Spark's endpoints named no scheme, so only
/// the default one ever ran and a registered handler was dead code) and M10 added this handler. Both
/// had unit coverage of their guards and <b>no demo registered either scheme</b>, so nothing ever
/// carried a real credential through the composite scheme, into <c>security.json</c>, and out the
/// other side of an ordinary endpoint. That is the gap these close.
/// </para>
/// <para>
/// This runs against the <b>strict</b> shared host — <c>SparkReplicationCertificateMode.Production</c>
/// — so the thumbprint pin is enforced, which is what makes the mismatch case below meaningful.
/// </para>
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ModuleCertificateCredentialTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ModuleCertificateCredentialTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>Fleet's security.json grants <c>Module:HR</c> exactly <c>ReadEditNew/Car</c> and <c>Replicate/Cars</c>.</summary>
    private const string GrantedModule = "HR";

    /// <summary>
    /// A self-signed certificate whose CN is the module name — the shape the mTLS guide tells
    /// operators to create, and the only part of the certificate this scheme reads for identity.
    /// </summary>
    private static X509Certificate2 NewModuleCertificate(string moduleName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={moduleName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        // Round-trip through PFX: on Windows the key of a freshly created certificate is not
        // usable for TLS client authentication until it has been persisted and reloaded.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
    }

    private HttpClient NewClient(X509Certificate2? certificate)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        if (certificate is not null)
            handler.ClientCertificates.Add(certificate);

        return new HttpClient(handler) { BaseAddress = new Uri(_fixture.Host.FleetUrl) };
    }

    private static string Thumbprint(X509Certificate2 certificate)
        => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>
    /// A create request in the shape the endpoint binds: the PersistentObject is wrapped, not posted
    /// bare. Posting it bare produces a 500 ("PersistentObject is required"), which looks nothing
    /// like an authorization outcome and would be easy to misread as one.
    /// </summary>
    private static object NewCarRequest()
        => new { persistentObject = CarFixture.New(CarFixture.RandomLicensePlate("CT")) };

    [Fact]
    public async Task A_module_certificate_authenticates_and_carries_its_security_json_rights()
    {
        using var certificate = NewModuleCertificate(GrantedModule);
        await _fixture.Host.SeedModuleAsync(GrantedModule, Thumbprint(certificate));
        using var client = NewClient(certificate);

        var response = await client.PostAsJsonAsync($"/spark/po/{CarFixture.TypeId}", NewCarRequest());

        // Two things at once, and both were untested. The certificate resolved to a principal at
        // all — which requires M9's composite scheme, since Spark's endpoints name no scheme and
        // the default alone would have left this caller anonymous. And the resulting
        // `group = "Module:HR"` claim was honoured by security.json, which grants New/Car.
        response.IsSuccessStatusCode.Should().BeTrue(
            "Module:HR is granted ReadEditNew/Car, so a caller carrying HR's registered "
            + "certificate may create one — got {0}.\n--- Fleet log ---\n{1}",
            response.StatusCode, _fixture.Host.RecentLog(25));
    }

    [Fact]
    public async Task A_module_certificate_is_exempt_from_antiforgery()
    {
        // The same request as above, stated as its own property because it is a different claim:
        // this POST carries no XSRF-TOKEN cookie and no X-XSRF-TOKEN header, and must still be
        // accepted. CSRF is an attack on *ambient* authority; a certificate cannot be attached to
        // a request by someone else's page, and demanding a cookie-derived token of a machine
        // caller that has no cookie is how external POSTs became impossible (D2/F8).
        using var certificate = NewModuleCertificate(GrantedModule);
        await _fixture.Host.SeedModuleAsync(GrantedModule, Thumbprint(certificate));
        using var client = NewClient(certificate);

        var response = await client.PostAsJsonAsync($"/spark/po/{CarFixture.TypeId}", NewCarRequest());

        // Asserting success, not "not 400". A refused credential also produces neither 400 nor 403
        // in some orderings, so the weaker form could pass while proving the opposite of the point.
        response.IsSuccessStatusCode.Should().BeTrue(
            "a non-ambient credential must not be asked for a token it cannot hold — got {0}.\n"
            + "--- Fleet log ---\n{1}", response.StatusCode, _fixture.Host.RecentLog(25));
    }

    [Fact]
    public async Task A_certificate_whose_CN_names_no_registered_module_is_not_authenticated()
    {
        // Deliberately not seeded. The certificate is perfectly valid and self-signed exactly like
        // the granted one — what it lacks is a registration, which is the whole trust decision.
        using var certificate = NewModuleCertificate($"Ghost-{Guid.NewGuid():N}");
        using var client = NewClient(certificate);

        var response = await client.PostAsJsonAsync($"/spark/po/{CarFixture.TypeId}", NewCarRequest());

        // 400, not 401 — and the difference is the point. A refused credential leaves the request on
        // the *anonymous* path, where the antiforgery gate answers before authorization ever runs:
        // an anonymous POST with no XSRF token is a 400. The certificate bought this caller nothing,
        // which is exactly the claim. (The granted certificate below gets past this gate precisely
        // because it authenticated and is non-ambient — that pair is the discriminator.)
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "presenting a certificate is not the same as being a known module, so this request is "
            + "treated as anonymous and stopped by antiforgery");
    }

    [Fact]
    public async Task A_certificate_that_does_not_match_the_pinned_thumbprint_is_refused()
    {
        // The case the whole pin exists for: right CN, wrong key. Registering one certificate and
        // presenting another must fail, or "registered module" would mean "anyone who can name it".
        using var registered = NewModuleCertificate(GrantedModule);
        using var impostor = NewModuleCertificate(GrantedModule);
        await _fixture.Host.SeedModuleAsync(GrantedModule, Thumbprint(registered));
        using var client = NewClient(impostor);

        var response = await client.PostAsJsonAsync($"/spark/po/{CarFixture.TypeId}", NewCarRequest());

        // Same anonymous-path 400 as the unregistered CN. Both refusals land in the same place,
        // which is correct: a certificate that fails the pin is not a weaker identity, it is none.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the pin is what makes a module certificate an identity rather than a claim");

        // Guard against the assertion passing for an unrelated reason: the impostor differs from
        // the registered certificate only in its key, so if this ever starts passing while the
        // granted-certificate test also passes, the difference really is the thumbprint.
        Thumbprint(impostor).Should().NotBe(Thumbprint(registered));
    }
}
