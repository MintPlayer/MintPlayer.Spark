using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>Phone verification (<c>docs/commerce_PRD.md</c> X13, D29). Opt-in.</summary>
public static class SparkPhoneVerificationExtensions
{
    /// <summary>
    /// Adds phone verification: <see cref="ISparkPhoneVerification"/>, and under <c>/spark/auth/manage</c> the
    /// routes <c>GET phone</c>, <c>POST phone/send</c> and <c>POST phone/verify</c>. A verified number lands on
    /// the user as <see cref="SparkUser.PhoneNumber"/> + <see cref="SparkUser.PhoneNumberConfirmed"/>.
    /// </summary>
    /// <remarks>
    /// Needs an <see cref="MintPlayer.Spark.Sms.ISparkSmsSender"/>: <c>services.AddSparkTestSms()</c> for tests and
    /// development, or a vendor adapter. Options bind from <c>Spark:Auth:PhoneVerification</c>, then
    /// <paramref name="configure"/>. Call before or after <c>AddAuthentication&lt;TUser&gt;()</c>; the user type is
    /// read when the service is first resolved.
    /// </remarks>
    public static ISparkBuilder AddPhoneVerification(this ISparkBuilder builder, Action<SparkPhoneVerificationOptions>? configure = null)
    {
        var registry = builder.Registry;
        AddPhoneVerificationServices(builder.Services, () => registry.IdentityUserType, configure);

        // Pending codes and send histories carry @expires: RavenDB reaps them after the send window.
        registry.RequireDocumentExpiration("AddPhoneVerification()");
        return builder;
    }

    /// <summary>The registrations of <see cref="AddPhoneVerification"/>; <paramref name="userType"/> is read when the service is first resolved.</summary>
    internal static void AddPhoneVerificationServices(IServiceCollection services, Func<Type?> userType, Action<SparkPhoneVerificationOptions>? configure)
    {
        if (services.Any(d => d.ServiceType == typeof(PhoneVerificationMarker)))
        {
            if (configure is not null)
                services.Configure(configure);
            return;
        }
        services.AddSingleton<PhoneVerificationMarker>();

        services.AddOptions<SparkPhoneVerificationOptions>();
        services.AddSingleton<IConfigureOptions<SparkPhoneVerificationOptions>>(sp => new ConfigureNamedOptions<SparkPhoneVerificationOptions>(
            Options.DefaultName,
            options => sp.GetService<IConfiguration>()?.GetSection(SparkPhoneVerificationOptions.SectionName).Bind(options)));
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISparkPhoneVerification>(sp =>
        {
            var type = userType() ?? throw new InvalidOperationException(
                "spark.AddPhoneVerification() needs spark.AddAuthentication<TUser>(): the verified number is stored on the user.");
            return (ISparkPhoneVerification)ActivatorUtilities.CreateInstance(sp, typeof(SparkPhoneVerification<>).MakeGenericType(type));
        });
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(SparkPhoneVerificationAccountDeletion<>)));
    }

    /// <summary>Marks phone verification as added; the endpoints' <c>IsEnabled</c> asks for it.</summary>
    internal sealed class PhoneVerificationMarker;
}

/// <summary>Account deletion: the account's pending code and send history go with it. The per-number history stays until it expires.</summary>
internal sealed class SparkPhoneVerificationAccountDeletion<TUser>(IDocumentStore store) : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;
        using var session = store.OpenAsyncSession();
        session.Delete(SparkPhoneVerificationState.IdFor(userId));
        await session.SaveChangesAsync(cancellationToken);
    }
}
