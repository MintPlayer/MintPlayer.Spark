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
    private sealed class RecordingEmailSender : Microsoft.AspNetCore.Identity.UI.Services.IEmailSender
    {
        public Task SendEmailAsync(string email, string subject, string htmlMessage) => Task.CompletedTask;
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
            .And.Contain("no email transport", "the message must name the cause, not just the mode");
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
    /// ⚠️ The guard matches <c>NoOpEmailSender</c> by type name, because it is internal to ASP.NET
    /// Core Identity UI and there is nothing public to compare against. An upstream rename would
    /// make the guard stop firing — which fails <b>open</b>, so this pins the name from the other
    /// side: if the framework's default sender is ever called something else, this fails and says
    /// where to look.
    /// </summary>
    [Fact]
    public async Task The_frameworks_default_sender_is_still_called_NoOpEmailSender()
    {
        using var host = BuildHost(SparkExternalLoginLinking.Disabled, registerTransport: false);
        await host.StartAsync();

        var sender = host.Services.GetRequiredService<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>();

        sender.GetType().FullName.Should().Be(
            "Microsoft.AspNetCore.Identity.UI.Services.NoOpEmailSender",
            "the ConfirmByEmail startup guard recognises it by this name, and a rename would make "
            + "the guard silently stop firing");
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
                        services.AddSingleton<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, RecordingEmailSender>();
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
