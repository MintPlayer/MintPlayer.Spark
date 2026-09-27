using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MintPlayer.Spark.Tests.Authentication;

/// <summary>
/// In-process coverage for the module-certificate scheme: CN → registered module → thumbprint pin.
/// <para>
/// The E2E suite does the real TLS handshake, but it runs Fleet in another process, so none of it
/// reaches the coverage report and a regression in the pin would only show up as a slow E2E failure.
/// No TLS is needed to reach the handler: <c>CertificateAuthenticationHandler</c> reads
/// <c>Connection.ClientCertificate</c> and requires <c>Request.IsHttps</c>, and TestServer lets
/// the test set both on the request directly.
/// </para>
/// </summary>
public class ModuleCertificateAuthenticationTests
{
    private const string Module = "HR";

    private readonly IModuleDirectory _directory = Substitute.For<IModuleDirectory>();

    private static X509Certificate2 NewCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName(subject), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        // Same PFX round-trip as the E2E helper: a freshly created key is not usable on Windows
        // until it has been persisted and reloaded.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
    }

    private static string Thumbprint(X509Certificate2 certificate)
        => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private void Register(string moduleName, string? thumbprint)
        => _directory.FindAsync(moduleName, Arg.Any<CancellationToken>()).Returns(new ModuleInformation
        {
            AppName = moduleName,
            AppUrl = $"https://{moduleName}.test",
            DatabaseName = $"Spark{moduleName}",
            ClientCertificateThumbprint = thumbprint,
        });

    private sealed record Outcome(bool Succeeded, string? Failure, IReadOnlyDictionary<string, string> Claims);

    /// <summary>
    /// Authenticates one HTTPS request carrying <paramref name="certificate"/> against the scheme,
    /// with the given mode and host environment, and reports the outcome.
    /// </summary>
    private async Task<Outcome> AuthenticateAsync(
        X509Certificate2 certificate,
        SparkReplicationCertificateMode mode,
        string environment = "Production")
    {
        var builder = new SparkBuilder(new ServiceCollection());
        builder.AddModuleCertificateAuthentication();

        AuthenticateResult? result = null;

        using var host = await new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    foreach (var descriptor in builder.Services)
                        services.Add(descriptor);
                    services.AddSingleton(_directory);
                    services.Configure<SparkReplicationOptions>(o => o.ClientCertificate.Mode = mode);
                })
                .Configure(app => app.Run(async context =>
                {
                    result = await context.AuthenticateAsync(SparkModuleCertificateDefaults.Scheme);
                })))
            .StartAsync();

        await host.GetTestServer().SendAsync(context =>
        {
            context.Request.Scheme = "https";
            context.Request.Method = "GET";
            context.Request.Path = "/";
            context.Connection.ClientCertificate = certificate;
        });

        result.Should().NotBeNull();
        return new Outcome(
            result!.Succeeded,
            result.Failure?.Message,
            result.Principal?.Claims.ToDictionary(c => c.Type, c => c.Value) ?? new Dictionary<string, string>());
    }

    [Fact]
    public async Task A_matching_pin_in_production_yields_a_module_principal()
    {
        using var certificate = NewCertificate($"CN={Module}");
        // Lower-cased on purpose: the pin is compared case-insensitively, and operators paste
        // thumbprints from tools that disagree on case.
        Register(Module, Thumbprint(certificate).ToLowerInvariant());

        var outcome = await AuthenticateAsync(certificate, SparkReplicationCertificateMode.Production);

        outcome.Succeeded.Should().BeTrue(outcome.Failure ?? "");
        outcome.Claims[ClaimTypes.NameIdentifier].Should().Be(Module);
        outcome.Claims[ClaimTypes.Name].Should().Be(Module);
        outcome.Claims["group"].Should().Be(SparkModuleCertificateDefaults.GroupPrefix + Module);
        outcome.Claims[SparkSystemContext.ClaimType].Should().Be("module");
    }

    [Fact]
    public async Task A_certificate_without_a_name_is_refused()
    {
        using var certificate = NewCertificate("");

        var outcome = await AuthenticateAsync(certificate, SparkReplicationCertificateMode.Production);

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Contain("no CN");
        await _directory.DidNotReceiveWithAnyArgs().FindAsync(default!, default);
    }

    [Fact]
    public async Task An_unregistered_module_is_refused_like_a_pin_mismatch()
    {
        using var certificate = NewCertificate("CN=Nobody");

        var outcome = await AuthenticateAsync(certificate, SparkReplicationCertificateMode.Development);

        // Development relaxes the pin, never the identity: an unknown CN fails in every mode.
        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be("Unrecognised module certificate.");
    }

    [Fact]
    public async Task A_different_certificate_for_a_registered_module_is_refused_in_production()
    {
        using var registered = NewCertificate($"CN={Module}");
        using var impostor = NewCertificate($"CN={Module}");
        Register(Module, Thumbprint(registered));

        var outcome = await AuthenticateAsync(impostor, SparkReplicationCertificateMode.Production);

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be("Unrecognised module certificate.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_module_with_no_pin_fails_closed_in_production(string? pin)
    {
        using var certificate = NewCertificate($"CN={Module}");
        Register(Module, pin);

        var outcome = await AuthenticateAsync(certificate, SparkReplicationCertificateMode.Production);

        outcome.Succeeded.Should().BeFalse("a module registered before pinning existed must not match everything");
    }

    [Fact]
    public async Task Development_mode_skips_the_pin_but_still_requires_a_registered_module()
    {
        using var certificate = NewCertificate($"CN={Module}");
        Register(Module, "00");

        var outcome = await AuthenticateAsync(certificate, SparkReplicationCertificateMode.Development);

        outcome.Succeeded.Should().BeTrue(outcome.Failure ?? "");
        outcome.Claims["group"].Should().Be("Module:HR");
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public async Task Auto_mode_follows_the_host_environment(string environment, bool accepted)
    {
        using var certificate = NewCertificate($"CN={Module}");
        Register(Module, "00");

        var outcome = await AuthenticateAsync(certificate, SparkReplicationCertificateMode.Auto, environment);

        outcome.Succeeded.Should().Be(accepted);
    }

    [Fact]
    public async Task A_directory_failure_is_an_authentication_failure_not_an_unhandled_exception()
    {
        using var certificate = NewCertificate($"CN={Module}");
        _directory.FindAsync(Module, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SparkModules unreachable"));

        var outcome = await AuthenticateAsync(certificate, SparkReplicationCertificateMode.Production);

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be("SparkModules unreachable");
    }
}
