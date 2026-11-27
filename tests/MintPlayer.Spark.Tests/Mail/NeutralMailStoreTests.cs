using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Deliveries;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #536: the mail manager's own documents on the neutral driver, the database <c>SPARK_TEST_DATABASE</c> picks.
/// <c>AddMailManager()</c> registers the delivery and suppression tables (D36); suppressions keep only the address's
/// hash, and a delivery record expires after the retention (<see cref="MintPlayer.Spark.Abstractions.IExpiring"/>).
/// </summary>
public class NeutralMailStoreTests : SparkTestDriver
{
    public sealed class MailContext : SparkContext;

    private SparkEndpointFactory<MailContext> NewHost() => new(
        Database,
        [],
        configureSpark: spark =>
        {
            spark.AddMessaging();
            spark.AddMailManager(o => o.From.Address = "noreply@app.example");
            spark.UsePickupFolderTransport(Path.Combine(Path.GetTempPath(), "spark-neutral-mail"));
        });

    [Fact]
    public async Task Suppressions_and_delivery_records_round_trip_on_the_store()
    {
        await using var host = NewHost();
        var store = host.GetService<ISparkStore>();
        var suppressions = new SparkMailSuppressions(store);

        (await suppressions.IsSuppressedAsync("Ann@App.example", "news")).Should().BeFalse();
        await suppressions.SuppressAsync(" ann@app.example ", SparkMailSuppressionReason.Unsubscribe, "news");
        (await suppressions.IsSuppressedAsync("ANN@app.example", "news")).Should().BeTrue("the address is normalized before hashing");
        (await suppressions.IsSuppressedAsync("ann@app.example", "billing")).Should().BeFalse("only the news stream");
        (await suppressions.RemoveAsync("ann@app.example", "news")).Should().BeTrue();
        (await suppressions.IsSuppressedAsync("ann@app.example", "news")).Should().BeFalse("the last stream went, and the document with it");

        var deliveries = new SparkMailDeliveryStore(store, Options.Create(new SparkMailOptions { DeliveryRetentionDays = 30 }));
        await deliveries.RecordAsync("d1", d => { d.Email = "ann@app.example"; d.Template = "Welcome"; d.Status = SparkMailDeliveryStatus.Sent; }, default);
        await deliveries.RecordAsync("d1", d => d.Status = SparkMailDeliveryStatus.Bounced, default);

        var record = await deliveries.LoadAsync("d1", default);
        record.Should().NotBeNull();
        record!.Template.Should().Be("Welcome");
        record.Status.Should().Be(SparkMailDeliveryStatus.Bounced);
        record.ExpiresAtUtc.HasValue.Should().BeTrue("set when the record is first written");
        record.ExpiresAtUtc!.Value.Should().BeAfter(DateTime.UtcNow.AddDays(29)).And.BeBefore(DateTime.UtcNow.AddDays(31));
    }
}
