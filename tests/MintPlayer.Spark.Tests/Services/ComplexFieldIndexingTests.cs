using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Why complex fields must be declared <c>FieldIndexing.No</c> (#273), measured against the real
/// engine. Every index here pins <c>SearchEngineType.Corax</c> explicitly: the fault is
/// engine-dependent — on a Lucene-defaulted server the verbatim map indexes fine, so without the
/// pin these tests pass vacuously and the regression they guard becomes invisible.
/// </summary>
public class ComplexFieldIndexingTests : SparkTestDriver
{
    public class Address
    {
        public string City { get; set; } = string.Empty;
        public string Street { get; set; } = string.Empty;
    }

    public class Job
    {
        public string Title { get; set; } = string.Empty;
    }

    public class Person
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Address? Address { get; set; }
        public Job[]? Jobs { get; set; }
    }

    /// <summary>The pre-#273 generated shape: complex fields mapped with default indexing.</summary>
    public class People_Verbatim : AbstractIndexCreationTask<Person>
    {
        public People_Verbatim()
        {
            Map = people => from p in people
                            select new { p.Name, p.Address, p.Jobs };
            StoreAllFields(FieldStorage.Yes);
            SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        }
    }

    /// <summary>The #273 repair: same map, complex fields declared <c>FieldIndexing.No</c>.</summary>
    public class People_Repaired : AbstractIndexCreationTask<Person>
    {
        public People_Repaired()
        {
            Map = people => from p in people
                            select new { p.Name, p.Address, p.Jobs };
            Index(nameof(VPerson.Address), FieldIndexing.No);
            Index(nameof(VPerson.Jobs), FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
            SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        }
    }

    [FromIndex(typeof(People_Repaired))]
    public class VPerson
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Address? Address { get; set; }
        public Job[]? Jobs { get; set; }
    }

    private async Task SeedAsync(bool withComplexValues)
    {
        using var session = Store.OpenAsyncSession();
        if (withComplexValues)
        {
            await session.StoreAsync(new Person
            {
                Name = "Alice",
                Address = new Address { City = "Ghent", Street = "Main" },
                Jobs = [new Job { Title = "Dev" }, new Job { Title = "Lead" }],
            });
        }
        await session.StoreAsync(new Person { Name = "Bob", Address = null, Jobs = null });
        await session.SaveChangesAsync();
    }

    private async Task<IndexErrors[]> WaitForIndexingOutcomeAsync(string indexName)
    {
        // A faulting index never goes non-stale for the faulted documents, so wait on either
        // errors appearing or the index settling, bounded.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var errors = await Store.Maintenance.SendAsync(new GetIndexErrorsOperation([indexName]));
            if (errors.Any(e => e.Errors.Length > 0)) return errors;

            var stats = await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation(indexName));
            if (!stats.IsStale) return errors;

            await Task.Delay(250);
        }
        return await Store.Maintenance.SendAsync(new GetIndexErrorsOperation([indexName]));
    }

    [Fact]
    public async Task Verbatim_complex_map_faults_per_document_on_Corax()
    {
        await SeedAsync(withComplexValues: true);
        await new People_Verbatim().ExecuteAsync(Store);

        var errors = await WaitForIndexingOutcomeAsync("People/Verbatim");

        errors.SelectMany(e => e.Errors).Should().NotBeEmpty(
            "Corax refuses to index a complex object with default options, per document");
        errors.SelectMany(e => e.Errors).Should().OnlyContain(e =>
            e.Error.Contains("complex") || e.Error.Contains("Corax"));
    }

    /// <summary>
    /// The failure profile that makes #273 vicious: an empty dev database (or one where the complex
    /// field is always null) indexes cleanly, and the fault only appears when real data arrives.
    /// </summary>
    [Fact]
    public async Task Verbatim_complex_map_does_not_fault_on_null_complex_values()
    {
        await SeedAsync(withComplexValues: false);
        await new People_Verbatim().ExecuteAsync(Store);

        var errors = await WaitForIndexingOutcomeAsync("People/Verbatim");

        errors.SelectMany(e => e.Errors).Should().BeEmpty();
        var stats = await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation("People/Verbatim"));
        stats.EntriesCount.Should().Be(1);
    }

    [Fact]
    public async Task FieldIndexingNo_repair_indexes_cleanly_and_projects_the_complex_fields()
    {
        await SeedAsync(withComplexValues: true);
        await new People_Repaired().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);

        var errors = await Store.Maintenance.SendAsync(new GetIndexErrorsOperation(["People/Repaired"]));
        errors.SelectMany(e => e.Errors).Should().BeEmpty();

        using var session = Store.OpenAsyncSession();
        var rows = await session.Query<VPerson, People_Repaired>()
            .ProjectInto<VPerson>()
            .ToListAsync();

        rows.Should().HaveCount(2);
        var alice = rows.Single(r => r.Name == "Alice");
        alice.Address.Should().NotBeNull("StoreAllFields keeps the complex field projectable");
        alice.Address!.City.Should().Be("Ghent");
        alice.Jobs.Should().HaveCount(2);
    }
}
