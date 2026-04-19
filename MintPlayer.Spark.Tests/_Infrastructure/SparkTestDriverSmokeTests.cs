using MintPlayer.Spark.Testing;

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
}
