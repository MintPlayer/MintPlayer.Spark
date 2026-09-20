using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Configuration;
using Raven.Client.Documents;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Xunit;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// The startup guard for <see cref="SparkExternalLoginLinking.ConfirmByEmail"/> without a mail
/// transport.
/// </summary>
/// <remarks>
/// <para>
/// ConfirmByEmail is only as good as the mail it sends — the link is made when a confirmation is
/// followed, so a discarded message means no link is ever made and nothing reports a failure. The
/// user sees a sign-in that appears to do nothing and the log stays clean, which is why this is a
/// startup error rather than a runtime one.
/// </para>
/// <para>
/// ⚠️ These pin the <em>subtlety</em> as much as the behaviour. The obvious guard — checking
/// <c>IEmailSender&lt;TUser&gt;</c> — would never fire, because that resolves to
/// <c>DefaultMessageEmailSender&lt;TUser&gt;</c> whether or not a transport exists. See
/// <c>EmailSenderRegistrationTests</c>, which measured it.
/// </para>
/// </remarks>
public class ConfirmByEmailStartupGuardTests : SparkTestDriver
{
    private sealed class RecordingLinkConfirmationSender : ISparkLinkConfirmationSender<SparkUser>
    {
        public Task SendLinkConfirmationAsync(
            SparkUser user, string providerDisplayName, string? providerIdentity,
            string confirmationLink, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>The failure this guard exists for: silent, total, and invisible in the log.</summary>
    [Fact]
    public async Task ConfirmByEmail_without_a_transport_is_refused_at_startup()
    {
        var act = async () => await StartAsync(
            linking: SparkExternalLoginLinking.ConfirmByEmail,
            registerTransport: false);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("ConfirmByEmail")
            .And.Contain("ISparkLinkConfirmationSender", "the message must name what to register, not just the mode");
    }

    [Fact]
    public async Task ConfirmByEmail_with_a_transport_starts()
        => await StartAsync(SparkExternalLoginLinking.ConfirmByEmail, registerTransport: true);

    /// <summary>
    /// The other modes send no confirmation, so a missing transport is not their problem. Guarding
    /// them too would refuse working configurations.
    /// </summary>
    [Theory]
    [InlineData(SparkExternalLoginLinking.Disabled)]
    [InlineData(SparkExternalLoginLinking.WhenSignedIn)]
    public async Task Other_modes_start_without_a_transport(SparkExternalLoginLinking linking)
        => await StartAsync(linking, registerTransport: false);

    /// <summary>
    /// Why the guard above can be a plain null check, recorded because the contrast is the design
    /// decision.
    /// </summary>
    /// <remarks>
    /// ASP.NET Identity <c>TryAdd</c>s a no-op sender, so an application with no mail transport
    /// still resolves one and absence is invisible — detectable only by recognising an internal
    /// type by name, which fails <b>open</b> if that type is ever renamed. Spark ships no default
    /// for <c>ISparkLinkConfirmationSender</c> precisely so its own guard does not inherit that
    /// problem. This pins the framework behaviour the contrast rests on.
    /// </remarks>
    [Fact]
    public async Task Identitys_mail_still_resolves_to_a_no_op_when_no_transport_is_registered()
    {
        using var host = BuildHost(SparkExternalLoginLinking.Disabled, registerTransport: false);
        await host.StartAsync();

        var sender = host.Services.GetRequiredService<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>();

        sender.GetType().FullName.Should().Be(
            "Microsoft.AspNetCore.Identity.UI.Services.NoOpEmailSender",
            "an absent transport is invisible through ASP.NET's contract, which is why Spark's own "
            + "contract has no default");

        await host.StopAsync();
    }

    private async Task StartAsync(SparkExternalLoginLinking linking, bool registerTransport)
    {
        using var host = BuildHost(linking, registerTransport);
        await host.StartAsync();
        await host.StopAsync();
    }

    private IHost BuildHost(SparkExternalLoginLinking linking, bool registerTransport)
        => new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAuthorization();
                    services.AddRouting();
                    services.Configure<SparkAuthenticationOptions>(options =>
                    {
                        // Local credentials stay Full so the unreachable-sign-in guard — a
                        // different check on the same path — cannot be what throws.
                        options.LocalCredentials = SparkLocalCredentials.Full;
                        options.ExternalLoginLinking = linking;
                    });

                    if (registerTransport)
                    {
                        services.AddSingleton<ISparkLinkConfirmationSender<SparkUser>, RecordingLinkConfirmationSender>();
                    }
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                        // The internal mapper directly: the guard lives there so it holds for every
                        // path that reaches it, including an application that maps the endpoints
                        // itself rather than going through AddSparkAuthentication.
                        endpoints.MapLocalCredentialApi<SparkUser>(SparkLocalCredentials.Full));
                }))
            .Build();
}
