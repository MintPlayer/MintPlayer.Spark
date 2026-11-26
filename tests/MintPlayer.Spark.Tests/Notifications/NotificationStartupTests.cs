using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.WebPush;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Notifications;

/// <summary>
/// Web Push's startup check runs on the root provider. AddMessaging registers the outbox scoped, over the scope's session
/// (#536 D50): the check asks whether it is registered and builds none, so Development (ValidateScopes) starts and no root
/// session opens.
/// </summary>
public class NotificationStartupTests
{
    [Fact]
    public void Web_push_finds_the_scoped_outbox_without_resolving_it_from_the_root()
    {
        var vapid = VapidDetails.Generate("mailto:ops@example.com");
        var services = new ServiceCollection();
        services.AddOptions<SparkNotificationsOptions>().Configure(o =>
        {
            o.WebPush.PublicKey = vapid.PublicKey;
            o.WebPush.PrivateKey = vapid.PrivateKey;
            o.WebPush.Subject = vapid.Subject;
        });
        services.AddScoped(_ => Substitute.For<IMessageOutbox>());
        services.AddScoped(_ => Substitute.For<ISparkAccountDirectory>());

        var act = () => SparkNotificationsExtensions.ValidateStartup(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));

        act.Should().NotThrow();
    }

    [Fact]
    public void Web_push_without_messaging_stops_startup()
    {
        var vapid = VapidDetails.Generate("mailto:ops@example.com");
        var services = new ServiceCollection();
        services.AddOptions<SparkNotificationsOptions>().Configure(o =>
        {
            o.WebPush.PublicKey = vapid.PublicKey;
            o.WebPush.PrivateKey = vapid.PrivateKey;
            o.WebPush.Subject = vapid.Subject;
        });

        var act = () => SparkNotificationsExtensions.ValidateStartup(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddMessaging*");
    }
}
