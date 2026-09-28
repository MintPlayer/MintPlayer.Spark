using System.Text.Json;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.ServerWide.Operations;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.History;

/// <summary>
/// #460 spikes for the History package (M7): H1 (revisions configuration and reads), H2 (a save's
/// change vector is the newest revision's) and H4 (reading and merging the revisions configuration
/// keeps what is there and is idempotent). Kept as tests so the answers stay pinned; the PRD records
/// the measured values (§4.1).
/// </summary>
/// <remarks>Fixture names start with <c>HSpike</c>: kept apart from every other fixture's types.</remarks>
public class HistorySpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private const string Docs = "HSpikeDocs";
    private const string Others = "HSpikeOthers";

    [Fact]
    public async Task H1_revisions_configuration_round_trips_and_revisions_read_back_newest_first()
    {
        output.WriteLine($"licence: {await LicenceTypeAsync()}");

        // Limits within the Community licence (at most 2 revisions / 45 days — measured below).
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration>
            {
                [Docs] = new() { Disabled = false },
                [Others] = new() { Disabled = false, MinimumRevisionsToKeep = 2, MinimumRevisionAgeToKeep = TimeSpan.FromDays(30), PurgeOnDelete = false },
            },
        }));
        var record = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));
        var config = record.Revisions!.Collections[Others];
        output.WriteLine($"read back: disabled={config.Disabled} min={config.MinimumRevisionsToKeep} age={config.MinimumRevisionAgeToKeep} purgeOnDelete={config.PurgeOnDelete}");

        var ids = new[] { "HSpikeDocs/a", "HSpikeDocs/b" };
        var changeVectors = new Dictionary<string, List<string>>();
        foreach (var id in ids)
        {
            changeVectors[id] = [];
            foreach (var name in new[] { "v1", "v2", "v3" })
            {
                using var session = Store.OpenAsyncSession();
                var doc = await session.LoadAsync<HSpikeDoc>(id) ?? new HSpikeDoc { Id = id };
                doc.Name = name;
                await session.StoreAsync(doc);
                await session.SaveChangesAsync();
                changeVectors[id].Add(session.Advanced.GetChangeVectorFor(doc));
            }
        }

        using var read = Store.OpenAsyncSession();
        var metadata = await read.Advanced.Revisions.GetMetadataForAsync("HSpikeDocs/a");
        var contents = await read.Advanced.Revisions.GetForAsync<HSpikeDoc>("HSpikeDocs/a");
        var listedVectors = metadata.Select(m => (string)m["@change-vector"]).ToList();
        output.WriteLine($"metadata keys: {string.Join(",", metadata[0].Keys.OrderBy(k => k))}");
        output.WriteLine($"metadata flags: {string.Join(" | ", metadata.Select(m => m.TryGetValue("@flags", out var f) ? f : "(none)"))}");
        output.WriteLine($"content order: {string.Join(",", contents.Select(c => c.Name))}");

        // A revision by change vector — and one that belongs to ANOTHER document.
        var own = await read.Advanced.Revisions.GetAsync<HSpikeDoc>(changeVectors["HSpikeDocs/a"][0]);
        var foreign = await read.Advanced.Revisions.GetAsync<HSpikeDoc>(changeVectors["HSpikeDocs/b"][0]);
        var foreignId = foreign is null ? null : read.Advanced.GetMetadataFor(foreign)["@id"]?.ToString();
        var unknown = await read.Advanced.Revisions.GetAsync<HSpikeDoc>("A:999999-AAAAAAAAAAAAAAAAAAAAAA");
        output.WriteLine($"by cv: own={own?.Name}/{own?.Id} foreign={foreign?.Name}/{foreign?.Id} foreign@id={foreignId} unknown={(unknown is null ? "null" : "found")}");
        output.WriteLine($"requests: {read.Advanced.NumberOfRequests}");

        config.Disabled.Should().BeFalse();
        config.MinimumRevisionsToKeep.Should().Be(2);
        config.MinimumRevisionAgeToKeep.Should().Be(TimeSpan.FromDays(30));
        metadata.Count.Should().Be(3);
        listedVectors.Should().Equal(Enumerable.Reverse(changeVectors["HSpikeDocs/a"]).ToList(), "revisions are listed newest first");
        contents.Select(c => c.Name).Should().Equal("v3", "v2", "v1");
        own!.Name.Should().Be("v1");
        foreign.Should().NotBeNull("a change vector is not scoped to a document id: the caller must check the id");
        foreignId.Should().Be("HSpikeDocs/b");
        unknown.Should().BeNull();
    }

    [Fact]
    public async Task H2_a_saves_change_vector_is_the_newest_revisions()
    {
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration> { [Docs] = new() { Disabled = false } },
        }));

        var saved = new List<string>();
        foreach (var name in new[] { "v1", "v2" })
        {
            using var session = Store.OpenAsyncSession();
            var doc = await session.LoadAsync<HSpikeDoc>("HSpikeDocs/h2") ?? new HSpikeDoc { Id = "HSpikeDocs/h2" };
            doc.Name = name;
            await session.StoreAsync(doc);
            await session.SaveChangesAsync();
            saved.Add(session.Advanced.GetChangeVectorFor(doc));
        }

        using var read = Store.OpenAsyncSession();
        var newest = (string)(await read.Advanced.Revisions.GetMetadataForAsync("HSpikeDocs/h2", 0, 1))[0]["@change-vector"];
        var current = read.Advanced.GetChangeVectorFor(await read.LoadAsync<HSpikeDoc>("HSpikeDocs/h2"));
        output.WriteLine($"saved={saved[^1]} newestRevision={newest} loaded={current}");

        newest.Should().Be(saved[^1]);
        current.Should().Be(saved[^1]);
    }

    [Fact]
    public async Task H4_ConfigureRevisionsOperation_replaces_the_whole_configuration_so_a_merge_must_read_first()
    {
        // Existing settings an operator made: a Default and another collection. (Within the Community
        // licence: an ENABLED default is refused there, a disabled one is accepted — measured in H1.)
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Default = new RevisionsCollectionConfiguration { Disabled = true, MinimumRevisionsToKeep = 1 },
            Collections = new Dictionary<string, RevisionsCollectionConfiguration>
            {
                [Others] = new() { Disabled = false, MinimumRevisionsToKeep = 2 },
            },
        }));
        var before = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));

        // (1) Sending only the new collection, as a naive configurator would.
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration> { [Docs] = new() { Disabled = false, MinimumRevisionsToKeep = 1 } },
        }));
        var naive = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));
        output.WriteLine($"naive: default={(naive.Revisions?.Default is null ? "null" : naive.Revisions.Default.MinimumRevisionsToKeep.ToString())} collections={string.Join(",", naive.Revisions!.Collections.Keys)}");

        // Put the operator's settings back, then (2) read-modify-write.
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(before.Revisions));
        var read = (await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database))).Revisions!;
        read.Collections[Docs] = new RevisionsCollectionConfiguration { Disabled = false, MinimumRevisionsToKeep = 1 };
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(read));
        var merged = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));

        // (3) The same merge again: equal → nothing to send. And what sending anyway costs.
        var again = (await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database))).Revisions!;
        var equal = JsonSerializer.Serialize(again.Collections[Docs]) == JsonSerializer.Serialize(new RevisionsCollectionConfiguration { Disabled = false, MinimumRevisionsToKeep = 1 });
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(again));
        var resent = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));
        output.WriteLine($"etags: before={before.Etag} merged={merged.Etag} resentUnchanged={resent.Etag} equalConfig={equal}");

        naive.Revisions.Default.Should().BeNull("ConfigureRevisionsOperation replaces the whole configuration");
        naive.Revisions.Collections.Keys.Should().Equal(Docs);
        merged.Revisions!.Default!.MinimumRevisionsToKeep.Should().Be(1);
        merged.Revisions.Default.Disabled.Should().BeTrue();
        merged.Revisions.Collections[Others].MinimumRevisionsToKeep.Should().Be(2);
        merged.Revisions.Collections[Docs].MinimumRevisionsToKeep.Should().Be(1);
        equal.Should().BeTrue();
        resent.Etag.Should().BeGreaterThan(merged.Etag, "an unchanged configuration sent again is still a database-record write");
    }

    [Fact]
    public async Task H1_which_revisions_configurations_the_licence_accepts()
    {
        output.WriteLine($"licence: {await LicenceTypeAsync()}");
        var probes = new (string Name, RevisionsConfiguration Config)[]
        {
            ("collection, no limits", new() { Collections = new() { [Docs] = new() { Disabled = false } } }),
            ("collection, MinimumRevisionsToKeep=100", new() { Collections = new() { [Docs] = new() { Disabled = false, MinimumRevisionsToKeep = 100 } } }),
            ("collection, MinimumRevisionAgeToKeep=90d", new() { Collections = new() { [Docs] = new() { Disabled = false, MinimumRevisionAgeToKeep = TimeSpan.FromDays(90) } } }),
            ("collection, PurgeOnDelete", new() { Collections = new() { [Docs] = new() { Disabled = false, PurgeOnDelete = true } } }),
            ("default, enabled", new() { Default = new() { Disabled = false } }),
            ("default, disabled", new() { Default = new() { Disabled = true } }),
            ("default + collection", new() { Default = new() { Disabled = false, MinimumRevisionsToKeep = 100 }, Collections = new() { [Others] = new() { Disabled = false } } }),
        };

        var accepted = new Dictionary<string, bool>();
        foreach (var (name, config) in probes)
        {
            try
            {
                await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(config));
                accepted[name] = true;
                output.WriteLine($"{name}: accepted");
            }
            catch (Exception ex)
            {
                accepted[name] = false;
                output.WriteLine($"{name}: refused — {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        accepted["collection, no limits"].Should().BeTrue("the configuration the History tests rely on");
    }

    [Fact]
    public async Task H4_the_database_level_revisions_config_endpoint_reads_the_configuration()
    {
        // GetDatabaseRecordOperation is a SERVER operation (operator clearance on a secured server);
        // is there a database-level read, so the configurator needs no more than ConfigureRevisions'
        // database-admin?
        using var http = new HttpClient();
        var url = $"{Store.Urls[0].TrimEnd('/')}/databases/{Store.Database}/revisions/config";
        var before = await http.GetAsync(url);
        output.WriteLine($"before any configuration: {(int)before.StatusCode} '{await before.Content.ReadAsStringAsync()}'");

        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration> { [Others] = new() { Disabled = false, MinimumRevisionsToKeep = 2 } },
        }));
        var after = await http.GetAsync(url);
        var body = await after.Content.ReadAsStringAsync();
        output.WriteLine($"after: {(int)after.StatusCode} {body}");

        after.IsSuccessStatusCode.Should().BeTrue();
        body.Should().Contain(Others);
    }

    private async Task<string> LicenceTypeAsync()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync(Store.Urls[0].TrimEnd('/') + "/license/status");
        using var doc = JsonDocument.Parse(json);
        // Only non-identifying fields: the status also names the licensee.
        return string.Join(", ", new[] { "Type", "Status", "Expired", "MaxCores" }
            .Select(name => $"{name}={(doc.RootElement.TryGetProperty(name, out var value) ? value.ToString() : "(absent)")}"));
    }
}

public class HSpikeDoc
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
