using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// For auth test hosts that map registration (<c>LocalCredentials = Full</c>) but are not about mail:
/// the #460 D6 startup guard refuses a registration surface whose account mail is discarded, so they
/// register this sink. A host that captures mail registers its own sender instead (later wins).
/// </summary>
internal static class TestMailSink
{
    /// <summary>Replaces Spark's MailManager-backed sender (no MailManager in these hosts) with a sink.</summary>
    public static IServiceCollection AddTestMailSink(this IServiceCollection services)
    {
        var last = services.LastOrDefault(d => d.ServiceType == typeof(IEmailSender<SparkUser>));
        if (last is null || last.ImplementationType == typeof(SparkMailEmailSender<SparkUser>))
            services.AddSingleton<IEmailSender<SparkUser>, DiscardingEmailSender>();
        return services;
    }

    private sealed class DiscardingEmailSender : IEmailSender<SparkUser>
    {
        public Task SendConfirmationLinkAsync(SparkUser user, string email, string confirmationLink) => Task.CompletedTask;
        public Task SendPasswordResetLinkAsync(SparkUser user, string email, string resetLink) => Task.CompletedTask;
        public Task SendPasswordResetCodeAsync(SparkUser user, string email, string resetCode) => Task.CompletedTask;
    }
}
