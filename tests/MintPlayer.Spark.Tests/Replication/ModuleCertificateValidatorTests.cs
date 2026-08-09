using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// In-process coverage for the mTLS gate (<see cref="ModuleCertificateValidator"/>), which
/// guards <c>/spark/etl/deploy</c> and <c>/spark/sync/apply</c> — the two endpoints that can
/// write any document in any collection.
/// <para>
/// These tests were previously unable to pin the Development branch at all: the validator was
/// built with <c>registrationService: null!</c>, on the stated grounds that the module lookup
/// only happened on the Production path. That was true, and it was the bug (F1) — the comment
/// in the validator claimed Development "still verifies the module is registered" while no such
/// check existed, so <c>{"RequestingModule": "anything"}</c> from an unauthenticated caller was
/// accepted. The test named <c>..._with_known_module_is_Ok</c> asserted exactly the behaviour
/// the defect produced, and a null service was proof nothing was ever looked up.
/// </para>
/// <para>
/// The directory is now substituted, so "known" and "unknown" are distinguishable and each
/// branch is pinned to the lookup it performs.
/// </para>
/// </summary>
public class ModuleCertificateValidatorTests
{
    private readonly IModuleDirectory _directory = Substitute.For<IModuleDirectory>();

    /// <summary>Makes <paramref name="moduleName"/> resolve as a registered module.</summary>
    private void Register(string moduleName, string? thumbprint = null)
        => _directory.FindAsync(moduleName, Arg.Any<CancellationToken>()).Returns(new ModuleInformation
        {
            AppName = moduleName,
            AppUrl = $"https://{moduleName}.test",
            DatabaseName = $"Spark{moduleName}",
            ClientCertificateThumbprint = thumbprint,
        });

    private ModuleCertificateValidator Build(SparkReplicationCertificateMode mode, string environment)
    {
        var options = Options.Create(new SparkReplicationOptions
        {
            ModuleName = "Fleet",
            ModuleUrl = "https://localhost:5001",
            ClientCertificate = new SparkReplicationCertificateOptions { Mode = mode },
        });

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        return new ModuleCertificateValidator(options, _directory, env, NullLogger<ModuleCertificateValidator>.Instance);
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

    /// <summary>
    /// F1. The whole point of Development mode over <c>Disabled</c>: the thumbprint check is
    /// relaxed, the identity check is not. Without this, setting
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c> — a variable that says nothing about mTLS —
    /// silently turns both replication endpoints into unauthenticated ones.
    /// </summary>
    [Fact]
    public async Task Development_refuses_a_module_that_never_registered()
    {
        var v = Build(SparkReplicationCertificateMode.Development, "Development");

        var result = await v.ValidateAsync(Ctx(), "Attacker-Not-Registered", CancellationToken.None);

        result.Should().Be(ModuleCertificateValidation.UnknownModule,
            "relaxing the certificate check must not also relax the question of who is calling");
    }

    [Fact]
    public async Task Development_accepts_a_registered_module_without_a_cert()
    {
        Register("HR");
        var v = Build(SparkReplicationCertificateMode.Auto, "Development");

        (await v.ValidateAsync(Ctx(), "HR", CancellationToken.None))
            .Should().Be(ModuleCertificateValidation.Ok);

        await _directory.Received(1).FindAsync("HR", Arg.Any<CancellationToken>());
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
        Register("HR", thumbprint: "AB12");
        var v = Build(SparkReplicationCertificateMode.Production, "Production");

        (await v.ValidateAsync(Ctx(), "HR", CancellationToken.None))
            .Should().Be(ModuleCertificateValidation.MissingCertificate);
    }

    /// <summary>
    /// The cert is absent, so the request cannot be authenticated regardless — but the
    /// refusal must not depend on the directory having been consulted, since a missing cert
    /// is decidable without it.
    /// </summary>
    [Fact]
    public async Task Production_does_not_reach_the_directory_when_no_cert_was_presented()
    {
        var v = Build(SparkReplicationCertificateMode.Production, "Production");

        await v.ValidateAsync(Ctx(), "HR", CancellationToken.None);

        await _directory.DidNotReceive().FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
