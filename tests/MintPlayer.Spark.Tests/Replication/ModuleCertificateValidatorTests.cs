using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// In-process coverage for the mTLS gate (ModuleCertificateValidator). Exercises the
/// mode-resolution and pre-RavenDB branches (Disabled / Development / Production with a
/// missing or empty module/cert); the thumbprint-lookup branches need a SparkModules
/// store and are covered by E2E.
/// </summary>
public class ModuleCertificateValidatorTests
{
    private static ModuleCertificateValidator Build(SparkReplicationCertificateMode mode, string environment)
    {
        var options = Options.Create(new SparkReplicationOptions
        {
            ModuleName = "Fleet",
            ModuleUrl = "https://localhost:5001",
            ClientCertificate = new SparkReplicationCertificateOptions { Mode = mode },
        });

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        // registrationService is only dereferenced on the Production thumbprint-lookup
        // path, which none of these cases reach — so a null is safe here.
        return new ModuleCertificateValidator(options, null!, env, NullLogger<ModuleCertificateValidator>.Instance);
    }

    private static HttpContext Ctx() => new DefaultHttpContext(); // Connection.ClientCertificate is null

    [Fact]
    public async Task Disabled_mode_is_passthrough_Ok()
    {
        var v = Build(SparkReplicationCertificateMode.Disabled, "Production");
        (await v.ValidateAsync(Ctx(), "anything", CancellationToken.None))
            .Should().Be(ModuleCertificateValidation.Ok);
    }

    [Fact]
    public async Task Auto_in_Development_with_empty_module_is_UnknownModule()
    {
        var v = Build(SparkReplicationCertificateMode.Auto, "Development");
        (await v.ValidateAsync(Ctx(), "", CancellationToken.None))
            .Should().Be(ModuleCertificateValidation.UnknownModule);
    }

    [Fact]
    public async Task Auto_in_Development_with_known_module_is_Ok_without_a_cert()
    {
        var v = Build(SparkReplicationCertificateMode.Auto, "Development");
        (await v.ValidateAsync(Ctx(), "HR", CancellationToken.None))
            .Should().Be(ModuleCertificateValidation.Ok);
    }

    [Fact]
    public async Task Auto_in_non_Development_resolves_to_Production_and_empty_module_is_UnknownModule()
    {
        var v = Build(SparkReplicationCertificateMode.Auto, "Production");
        (await v.ValidateAsync(Ctx(), "", CancellationToken.None))
            .Should().Be(ModuleCertificateValidation.UnknownModule);
    }

    [Fact]
    public async Task Production_with_a_module_but_no_client_cert_is_MissingCertificate()
    {
        var v = Build(SparkReplicationCertificateMode.Production, "Production");
        (await v.ValidateAsync(Ctx(), "HR", CancellationToken.None))
            .Should().Be(ModuleCertificateValidation.MissingCertificate);
    }
}
