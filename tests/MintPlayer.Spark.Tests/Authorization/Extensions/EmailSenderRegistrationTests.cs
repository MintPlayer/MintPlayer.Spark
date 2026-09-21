using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Pins what <c>IEmailSender&lt;SparkUser&gt;</c> resolves to when an application registers Spark
/// authentication and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the answer is a trap rather than a curiosity. <c>AddApiEndpoints</c>, which
/// <see cref="SparkAuthenticationExtensions.AddSparkAuthentication{TUser}"/> reaches through
/// <c>AddIdentityApiEndpoints</c>, <c>TryAdd</c>s a <em>no-op</em> sender. So an application that
/// has never registered a transport still resolves a sender successfully, and every call to it
/// reports success and delivers nothing. There is no exception, no log line, and no failed health
/// check — the only symptom is that mail never arrives.
/// </para>
/// <para>
/// That matters for any feature that mails a user (email confirmation, the confirm-by-email account
/// linking flow): "the send returned without throwing" is not evidence of anything. A feature
/// depending on delivery needs a startup guard that refuses to run against the no-op, and that
/// guard needs to know precisely what the no-op looks like — which is what this test records.
/// </para>
/// </remarks>
public class EmailSenderRegistrationTests : SparkTestDriver
{
    private async Task<IHost> StartAsync(Action<IServiceCollection>? configure = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAuthorization();
                    services.AddRouting();
                    configure?.Invoke(services);
                })
                .Configure(app => app.UseRouting()))
            .StartAsync();
    }

    /// <summary>
    /// The headline: a sender IS resolvable without anyone registering one, and it is a framework
    /// default rather than anything the application chose.
    /// </summary>
    [Fact]
    public async Task A_sender_resolves_even_though_no_transport_was_registered()
    {
        using var host = await StartAsync();

        var sender = host.Services.GetService<IEmailSender<SparkUser>>();

        sender.Should().NotBeNull(
            "AddApiEndpoints TryAdds a sender, so resolution succeeding proves nothing about delivery");
    }

    /// <summary>
    /// ⚠️ The generic sender is <em>always</em> <c>DefaultMessageEmailSender&lt;TUser&gt;</c>, whether
    /// or not a transport exists. A startup guard written against this type would therefore never
    /// fire — which is the mistake this test exists to prevent.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-20 on net10.0: the resolved type is
    /// <c>Microsoft.AspNetCore.Identity.DefaultMessageEmailSender`1[SparkUser]</c>. It is a thin
    /// adapter that formats the three Identity messages and hands them to the <em>non-generic</em>
    /// <c>IEmailSender</c> — so the presence or absence of a real transport is invisible here.
    /// </remarks>
    [Fact]
    public async Task The_generic_sender_is_always_the_framework_adapter()
    {
        using var host = await StartAsync();

        var sender = host.Services.GetRequiredService<IEmailSender<SparkUser>>();
        var senderTypeName = sender.GetType().FullName ?? sender.GetType().Name;

        // Asserted on the name because DefaultMessageEmailSender<T> is internal to the framework
        // and cannot be named from here.
        senderTypeName.Should().Contain("DefaultMessageEmailSender",
            "the generic sender is an adapter, so it says nothing about whether mail can be delivered");
    }

    /// <summary>
    /// The real discriminator: with no transport registered, the <em>non-generic</em>
    /// <c>IEmailSender</c> is the framework's <c>NoOpEmailSender</c>, which accepts every message
    /// and discards it.
    /// </summary>
    /// <remarks>
    /// This is the type a startup guard must look for. If a framework upgrade renames or replaces
    /// it, this test goes red — which is the only warning anyone would get, because in production
    /// the symptom is silent: sends keep succeeding and mail simply never arrives.
    /// </remarks>
    [Fact]
    public async Task With_no_transport_the_non_generic_sender_is_the_no_op()
    {
        using var host = await StartAsync();

        var inner = host.Services.GetRequiredService<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>();
        var innerTypeName = inner.GetType().FullName ?? inner.GetType().Name;

        innerTypeName.Should().Be("Microsoft.AspNetCore.Identity.UI.Services.NoOpEmailSender",
            "this exact type is what the confirm-by-email startup guard has to refuse to run against");
    }

    /// <summary>
    /// The escape hatch an application uses. Registering a transport must win over the framework's
    /// TryAdd — if it ever stopped doing so, every application's mail would silently revert to the
    /// no-op.
    /// </summary>
    [Fact]
    public async Task An_application_registered_sender_wins_over_the_framework_default()
    {
        using var host = await StartAsync(services =>
            services.AddSingleton<IEmailSender<SparkUser>, RecordingEmailSender>());

        var sender = host.Services.GetRequiredService<IEmailSender<SparkUser>>();

        sender.Should().BeOfType<RecordingEmailSender>(
            "an application that registers a transport must not be silently overridden");
    }

    /// <summary>A transport that records instead of sending, standing in for a real one.</summary>
    private sealed class RecordingEmailSender : IEmailSender<SparkUser>
    {
        public List<(string Email, string Subject)> Sent { get; } = [];

        public Task SendConfirmationLinkAsync(SparkUser user, string email, string confirmationLink)
        {
            Sent.Add((email, "confirmation"));
            return Task.CompletedTask;
        }

        public Task SendPasswordResetLinkAsync(SparkUser user, string email, string resetLink)
        {
            Sent.Add((email, "reset-link"));
            return Task.CompletedTask;
        }

        public Task SendPasswordResetCodeAsync(SparkUser user, string email, string resetCode)
        {
            Sent.Add((email, "reset-code"));
            return Task.CompletedTask;
        }
    }
}
