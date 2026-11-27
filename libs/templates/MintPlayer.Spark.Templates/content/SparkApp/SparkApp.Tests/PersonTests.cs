using MintPlayer.Spark.Testing;

namespace SparkApp.Tests;

/// <summary>
/// A sample test. Each test gets its own database; override <c>IndexAssemblies</c> to deploy your indexes first, and
/// use <c>SparkEndpointFactory</c> to call the app's endpoints. The MintPlayer.Spark.Testing README has the rest.
/// </summary>
public class PersonTests : RavenSparkTestDriver
{
    [Fact]
    public async Task A_stored_person_loads_back()
    {
        string id;
        using (var session = Store.OpenAsyncSession())
        {
            var person = new Person { FirstName = "Ada", LastName = "Lovelace" };
            await session.StoreAsync(person);
            await session.SaveChangesAsync();
            id = person.Id!;
        }

        using var check = Store.OpenAsyncSession();
        var loaded = await check.LoadAsync<Person>(id);

        Assert.Equal("Lovelace", loaded.LastName);
    }
}
