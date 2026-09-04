using System.Security.Claims;
using CodeCoverage.ApiTokens;
using CodeCoverage.Controllers;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// GET /api/uploads/capabilities — how an action discovers what the server it is
/// actually talking to can do.
/// <para>
/// The action is consumed from a git ref while this server ships as a docker
/// image the VPS pulls, so the two versions are never guaranteed to match. These
/// tests pin the half of that contract living here: the endpoint needs no
/// database, never writes, reports a contract integer, and only advertises
/// features that are really implemented.
/// </para>
/// </summary>
public class UploadsControllerCapabilitiesTests
{
    /// <summary>Capabilities is a pure read; the bus only serves the POST paths.</summary>
    private sealed class NullMessageBus : IMessageBus
    {
        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

            public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, string queueName, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
    }

    /// <summary>
    /// Deliberately null: reaching a session would be the defect. A probe that
    /// touched RavenDB would put a database round-trip in front of every upload,
    /// and would fail exactly when a client most needs to know what it is talking
    /// to.
    /// </summary>
    private static UploadsController CreateController()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAsyncDocumentSession>(_ => null!);
        services.AddSingleton<IMessageBus>(new NullMessageBus());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IGitHubDiffService>(new Services.ScriptedDiffService());
        services.AddScoped<IBaseResolver, BaseResolver>();
        services.AddScoped<UploadsController>();

        var controller = services.BuildServiceProvider().GetRequiredService<UploadsController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account")],
                    ApiTokenAuthenticationHandler.SchemeName)),
            },
        };
        return controller;
    }

    private static UploadsController.CapabilitiesResponse Body(
        ActionResult<UploadsController.CapabilitiesResponse> result)
        => (UploadsController.CapabilitiesResponse)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public void Reports_a_contract_version()
    {
        var response = Body(CreateController().Capabilities());

        // A client treats a missing endpoint as contract 0, so the served value
        // must be at least 1 or an up-to-date server is indistinguishable from an
        // image that predates the endpoint entirely.
        Assert.True(response.Contract >= 1);
    }

    [Fact]
    public void Advertises_partial_uploads_because_they_are_implemented()
    {
        var response = Body(CreateController().Capabilities());

        // The one feature the action branches on today: without it, `partial:
        // true` is silently dropped and a subset gets compared against a
        // whole-workspace baseline.
        Assert.Contains("partial-uploads", response.Features);
    }

    [Fact]
    public void Advertises_only_named_features()
    {
        var response = Body(CreateController().Capabilities());

        Assert.All(response.Features, feature =>
        {
            Assert.False(string.IsNullOrWhiteSpace(feature));
            // Lower-kebab, like the flag vocabulary — a client compares these as
            // literals, so casing drift is a silent mismatch.
            Assert.Matches("^[a-z0-9-]+$", feature);
        });
        Assert.Equal(response.Features.Length, response.Features.Distinct().Count());
    }

    /// <summary>
    /// The probe runs in front of every upload, including on a server whose
    /// database is unreachable. Constructing the controller with a null session
    /// and still answering proves the read is genuinely pure.
    /// </summary>
    [Fact]
    public void Needs_no_database()
    {
        var controller = CreateController();

        var result = controller.Capabilities();

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
