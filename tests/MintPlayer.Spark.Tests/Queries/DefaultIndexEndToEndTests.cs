using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Queries;

/// <summary>
/// The default-index features end to end (<c>docs/default_index_PRD.md</c>, M8): synchronize a context into a model, boot a
/// Spark host on that model, and run the queries it minted. The parts have their own tests (the catalog's election in
/// <c>IndexCatalogTests</c>, the generator's claim suppression in <c>GenerateIndexGeneratorTests</c>, the reference map in
/// <c>SynchronizeReferenceRootTests</c>); these check that they compose.
/// </summary>
/// <remarks>
/// ⚠️ Every test host scans this whole assembly for indexes, so the fixture indexes here take part in every host's catalog
/// election: each claims only its own fixture type (PRD §7, M1/M2 notes).
/// </remarks>
public class DefaultIndexEndToEndTests : SparkTestDriver
{
    // ---- (a) A hand-written claim beats a [GenerateIndex] index (D5) ------------------------------------------------

    /// <summary>
    /// A <c>[GenerateIndex]</c> entity. The test project does not run the Spark generator, so its output is spelled out
    /// below; the shape of HR's <c>Profession</c>, the demo of this feature.
    /// </summary>
    [GenerateIndex]
    public class DeProfession
    {
        public string? Id { get; set; }
        public string Description { get; set; } = "";
        public string Regime { get; set; } = "";
    }

    /// <summary>
    /// What <c>[GenerateIndex]</c> emits for <see cref="DeProfession"/> once a hand-written index claims it: the index and
    /// its view, <b>without</b> <c>[DefaultIndex]</c> (D5; <c>GenerateIndexGeneratorTests.A_same_project_claim_suppresses_the_generated_marker</c>).
    /// Before D5 it carried the marker, and with the claim below that was two claims for one type.
    /// </summary>
    public class DeProfessions_Overview : SparkIndexCreationTask<DeProfession>
    {
        public DeProfessions_Overview()
        {
            Map = professions => from p in professions select new VDeProfession { Id = p.Id, Description = p.Description, Regime = p.Regime };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(DeProfessions_Overview))]
    public class VDeProfession
    {
        public string? Id { get; set; }
        public string Description { get; set; } = "";
        public string Regime { get; set; } = "";
    }

    /// <summary>The hand-written claim: it computes a label the generated index does not have.</summary>
    [DefaultIndex(typeof(DeProfession))]
    public class DeProfessions_Lookup : SparkIndexCreationTask<DeProfession>
    {
        public DeProfessions_Lookup()
        {
            Map = professions => from p in professions
                                 select new VDeProfessionLookup { Id = p.Id, Description = p.Description, Regime = p.Regime, Label = p.Description + " (" + p.Regime + ")" };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(DeProfessions_Lookup))]
    public class VDeProfessionLookup
    {
        public string? Id { get; set; }
        public string Description { get; set; } = "";
        public string Regime { get; set; } = "";
        public string Label { get; set; } = "";
    }

    [SparkPlainRoot(typeof(DeProfession), nameof(DeProfessions))]
    public class DeProfessionContext : SparkContext
    {
        public IRavenQueryable<DeProfession> DeProfessions => Session.Query<DeProfession>();
    }

    [Fact]
    public async Task A_hand_claimed_default_beats_the_generated_index_from_synchronize_to_query()
    {
        using var scratch = new ScratchModel();
        Synchronize(scratch, typeof(DeProfessionContext), catalog =>
        {
            catalog.RegisterIndex(typeof(DeProfessions_Overview));
            catalog.RegisterIndex(typeof(DeProfessions_Lookup));
            catalog.RegisterProjection(typeof(VDeProfession), typeof(DeProfessions_Overview));
            catalog.RegisterProjection(typeof(VDeProfessionLookup), typeof(DeProfessions_Lookup));
        });
        var model = scratch.Read(nameof(DeProfession));
        model.PersistentObject.IndexName.Should().Be(nameof(DeProfessions_Lookup), "the claim shapes the model");
        model.PersistentObject.QueryType.Should().Be(typeof(VDeProfessionLookup).FullName);

        var factory = Track(new SparkEndpointFactory<DeProfessionContext>(Store, [model], configureIndexCatalog: catalog =>
        {
            catalog.RegisterIndex(typeof(DeProfessions_Overview));
            catalog.RegisterIndex(typeof(DeProfessions_Lookup));
        }));

        // The host started: the scanned catalog holds both viewed indexes of DeProfession and elected the claim. Without
        // the claim, two viewed indexes and none marked is a startup error (D3); with the generated marker too, two claims.
        factory.GetService<IIndexCatalog>().GetDefaultFor(typeof(DeProfession))!.IndexType.Should().Be(typeof(DeProfessions_Lookup));

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new DeProfession { Description = "Nurse", Regime = "Employee" }, "deprofessions/1");
            await session.StoreAsync(new DeProfession { Description = "Nurse", Regime = "Freelance" }, "deprofessions/2");
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        var query = model.Queries.Single(q => q.Name == "GetDeProfessions");
        query.IndexName.Should().Be(nameof(DeProfessions_Lookup), "synchronize stamps the elected default on the root's query");
        var result = await factory.GetService<IQueryExecutor>().ExecuteQueryAsync(query, skip: 0, take: 50);

        result.Items.Select(i => i.Values.Single(v => v.Key == "Label").Value?.ToString()).Should().BeEquivalentTo(["Nurse (Employee)", "Nurse (Freelance)"],
            because: "the rows come from the claimed index, the only one that computes Label");
    }

    // ---- (b) A query-less reference resolves to the plain root, and its picker query runs (D6) ----------------------

    public class DeOrganization
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
    }

    public class DeSite
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";

        [Reference(typeof(DeOrganization))]
        public string? OrganizationId { get; set; }
    }

    /// <summary>
    /// Two roots of <see cref="DeOrganization"/>; the filtered one is declared last, which the synchronizer's old last-wins
    /// map picked. The markers are what the generator emits for the plain roots.
    /// </summary>
    [SparkPlainRoot(typeof(DeSite), nameof(DeSites))]
    [SparkPlainRoot(typeof(DeOrganization), nameof(DeOrganizations))]
    public class DeSiteContext : SparkContext
    {
        public IRavenQueryable<DeSite> DeSites => Session.Query<DeSite>();
        public IRavenQueryable<DeOrganization> DeOrganizations => Session.Query<DeOrganization>();
        public IRavenQueryable<DeOrganization> ActiveDeOrganizations => Session.Query<DeOrganization>().Where(o => o.IsActive);
    }

    [Fact]
    public async Task A_query_less_reference_resolves_to_the_plain_root_and_its_picker_query_lists_every_row()
    {
        using var scratch = new ScratchModel();
        Synchronize(scratch, typeof(DeSiteContext), _ => { });
        var site = scratch.Read(nameof(DeSite));
        var organization = scratch.Read(nameof(DeOrganization));

        var referenceQuery = site.PersistentObject.Attributes.Single(a => a.Name == nameof(DeSite.OrganizationId)).Query;
        referenceQuery.Should().Be("GetDeOrganizations");

        var factory = Track(new SparkEndpointFactory<DeSiteContext>(Store, [site, organization]));
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new DeOrganization { Name = "Acme", IsActive = true }, "deorganizations/1");
            await session.StoreAsync(new DeOrganization { Name = "Dormant", IsActive = false }, "deorganizations/2");
            await session.SaveChangesAsync();

            // The picker query sorts by Name, which RavenDB serves from an auto index: create it and let it catch up.
            await session.Query<DeOrganization>().Customize(c => c.WaitForNonStaleResults()).OrderBy(o => o.Name).ToListAsync();
        }

        // The client fetches the picker's options by the attribute's query name (spark-po-form, executeQueryByName).
        var picker = factory.GetService<IQueryLoader>().GetQueryByName(referenceQuery!);
        picker.Should().NotBeNull();
        picker!.Source.Should().Be($"Database.{nameof(DeSiteContext.DeOrganizations)}");
        var result = await factory.GetService<IQueryExecutor>().ExecuteQueryAsync(picker, skip: 0, take: 50);

        result.Items.Select(i => i.Id!).Should().BeEquivalentTo(["deorganizations/1", "deorganizations/2"],
            because: "the plain root lists every organization; ActiveDeOrganizations would have dropped the dormant one");
    }

    // ---- infrastructure -----------------------------------------------------------------------------------------------

    private readonly List<IAsyncDisposable> factories = [];

    private T Track<T>(T factory) where T : IAsyncDisposable
    {
        factories.Add(factory);
        return factory;
    }

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories) await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private sealed class ScratchModel : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "default-index-e2e-" + Guid.NewGuid().ToString("N"));

        public ScratchModel() => Directory.CreateDirectory(Root);

        public EntityTypeFile Read(string type)
            => JsonSerializer.Deserialize<EntityTypeFile>(
                File.ReadAllText(Path.Combine(Root, "App_Data", "Model", type + ".json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>What <c>--spark-synchronize-model</c> does, into <paramref name="scratch"/>, over a catalog of the given indexes.</summary>
    private static void Synchronize(ScratchModel scratch, Type context, Action<IndexCatalog> register)
    {
        var catalog = new IndexCatalog();
        register(catalog);
        catalog.Freeze();
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(scratch.Root);
        new ModelSynchronizer(environment, catalog).SynchronizeModels(context);
    }
}
