using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests._Infrastructure;

public class SparkTestDriverSmokeTests : SparkTestDriver
{
    private class Widget
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public async Task Document_store_round_trips_a_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Widget { Name = "Acme" }, "widgets/1");
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<Widget>("widgets/1");
            loaded.Should().NotBeNull();
            loaded!.Name.Should().Be("Acme");
        }
    }

    /// <summary>
    /// The host resolves the driver's own <see cref="SparkTestDriver.Store"/>, not a second store of
    /// its own — so a fixture written through <c>Store</c> and a service reading through
    /// <c>IDatabaseAccess</c> are looking at one database.
    /// </summary>
    /// <remarks>
    /// ⚠️ Pinned because it is invisible at the call site and silent when wrong.
    /// <c>AddSpark</c> registers its own <see cref="IDocumentStore"/> from configuration, and
    /// <see cref="SparkEndpointFactory{TContext}"/> removes that registration and substitutes the
    /// driver's. If that substitution ever regressed, nothing would throw: writes through
    /// <c>Store</c> and writes through the host would land in different databases, and every
    /// fixture would simply read back as absent. The scoped session is asserted too, since it is the
    /// one every request-path service actually goes through.
    /// </remarks>
    [Fact]
    public async Task The_host_uses_the_drivers_store()
    {
        await using var factory = new SparkEndpointFactory(Store, []);

        factory.GetService<IDocumentStore>().Should().BeSameAs(Store);

        using var scope = factory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IAsyncDocumentSession>();
        session.Advanced.DocumentStore.Should().BeSameAs(Store);
    }
}
