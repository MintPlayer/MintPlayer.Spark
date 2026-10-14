using System.Reflection;
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
/// A <c>[DefaultIndex]</c> on an index without a view (<c>docs/default_index_PRD.md</c>, D3; SP4): it is the
/// type's default, the model file is bound to it, its queries return entity documents, and its query surface
/// is the fields its map emits — attributes outside it are minted not sortable and not filterable, and the
/// default sort comes only from emitted fields, because RavenDB answers a sort on anything else with a 500.
/// </summary>
public class ViewlessDefaultIndexTests : SparkTestDriver
{
    public class VlBook
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
    }

    /// <summary>Marked, no view, and emits Title only.</summary>
    [DefaultIndex]
    public class VlBooks_ByTitle : SparkIndexCreationTask<VlBook>
    {
        public VlBooks_ByTitle()
        {
            Map = books => from b in books select new { b.Title };
        }
    }

    /// <summary>An unmarked sibling with a view: the implicit default, were it not for the claim.</summary>
    public class VlBooks_Overview : SparkIndexCreationTask<VlBook>
    {
        public VlBooks_Overview()
        {
            Map = books => from b in books select new { b.Name, b.Title, b.Author };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(VlBooks_Overview))]
    public class VVlBook
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
    }

    public class VlBookContext : SparkContext
    {
        public IRavenQueryable<VlBook> VlBooks => Session.Query<VlBook>();
    }

    private static IndexCatalog Catalog()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterIndex(typeof(VlBooks_ByTitle));
        catalog.RegisterIndex(typeof(VlBooks_Overview));
        catalog.RegisterProjection(typeof(VVlBook), typeof(VlBooks_Overview));
        catalog.Freeze();
        return catalog;
    }

    // ---- Synchronizer and dev gate (no server) ----

    private sealed class ScratchModel : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "viewless-default-" + Guid.NewGuid().ToString("N"));
        public string ModelFile => Path.Combine(Root, "App_Data", "Model", nameof(VlBook) + ".json");

        public ScratchModel() => Directory.CreateDirectory(Root);

        public EntityTypeFile Read()
            => JsonSerializer.Deserialize<EntityTypeFile>(File.ReadAllText(ModelFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Synchronize(ScratchModel scratch, IIndexCatalog catalog)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(scratch.Root);
        new ModelSynchronizer(environment, catalog).SynchronizeModels(typeof(VlBookContext));
    }

    [Fact]
    public void Synchronize_binds_the_viewless_default_and_mints_its_query_surface()
    {
        using var scratch = new ScratchModel();

        Synchronize(scratch, Catalog());
        var file = scratch.Read();

        file.PersistentObject.IndexName.Should().Be(nameof(VlBooks_ByTitle), "a viewless default keeps its binding (SP4)");
        file.PersistentObject.QueryType.Should().BeNull("a viewless default shapes nothing");

        var attributes = file.PersistentObject.Attributes.ToDictionary(a => a.Name);
        attributes[nameof(VlBook.Title)].CanSort.HasValue.Should().BeFalse("Title is emitted by the map");
        attributes[nameof(VlBook.Title)].CanFilter.HasValue.Should().BeFalse();
        attributes[nameof(VlBook.Author)].CanSort.Should().Be(false, "the map does not emit Author");
        attributes[nameof(VlBook.Author)].CanFilter.Should().Be(false);
        attributes[nameof(VlBook.Name)].CanSort.Should().Be(false);

        var query = file.Queries!.Single(q => q.Name == "GetVlBooks");
        query.IndexName.Should().Be(nameof(VlBooks_ByTitle));
        query.SortColumns.Select(s => s.Property).Should().BeEquivalentTo([nameof(VlBook.Title)],
            because: "the default sort would pick Name, which the map does not emit");
    }

    [Fact]
    public void Synchronize_leaves_hand_set_capabilities_alone_on_a_second_run()
    {
        using var scratch = new ScratchModel();
        Synchronize(scratch, Catalog());
        var first = File.ReadAllText(scratch.ModelFile);

        Synchronize(scratch, Catalog());

        File.ReadAllText(scratch.ModelFile).Should().Be(first, "minting is creation-only, so synchronize reaches a fixed point");
    }

    [Fact]
    public void The_model_hash_records_the_viewless_default()
    {
        using var scratch = new ScratchModel();
        Synchronize(scratch, Catalog());
        var withDefault = ModelSynchronizer.BuildModelHashes(typeof(VlBookContext), Catalog(), scratch.Root).ModelHash;

        var overviewOnly = new IndexCatalog();
        overviewOnly.RegisterIndex(typeof(VlBooks_Overview));
        overviewOnly.RegisterProjection(typeof(VVlBook), typeof(VlBooks_Overview));
        overviewOnly.Freeze();
        var withoutClaim = ModelSynchronizer.BuildModelHashes(typeof(VlBookContext), overviewOnly, scratch.Root).ModelHash;

        withDefault.Should().NotBe(withoutClaim, "the hash moves exactly when the model's indexName does");
    }

    [Fact]
    public void The_dev_gate_accepts_a_viewless_default_but_holds_the_model_to_its_surface()
    {
        using var scratch = new ScratchModel();
        var catalog = Catalog();
        Synchronize(scratch, catalog);

        RunGate(catalog, scratch.Root).ExitCode.Should().Be(0, "the synchronized model stays inside the emitted fields");

        // An author's sort on a field the map does not emit, and an attribute whose flags were lifted.
        var json = File.ReadAllText(scratch.ModelFile)
            .Replace("\"property\": \"Title\"", "\"property\": \"Author\"", StringComparison.Ordinal);
        File.WriteAllText(scratch.ModelFile, json);

        var (exitCode, stderr) = RunGate(catalog, scratch.Root);
        exitCode.Should().Be(3);
        stderr.Should().Contain("GetVlBooks: sorts on 'Author'");
    }

    private static readonly object GateLock = new();

    private static (int ExitCode, string Stderr) RunGate(IIndexCatalog catalog, string root)
    {
        var gate = typeof(SparkDevelopmentExtensions)
            .GetMethod("VerifyBoundIndexHasAProjection", BindingFlags.NonPublic | BindingFlags.Static)!;

        // The gate reports through Environment.ExitCode and stderr, both process-wide.
        lock (GateLock)
        {
            var savedError = Console.Error;
            var savedExit = Environment.ExitCode;
            var error = new StringWriter();
            Environment.ExitCode = 0;
            Console.SetError(error);
            try
            {
                gate.Invoke(null, [catalog, root]);
                return (Environment.ExitCode, error.ToString());
            }
            finally
            {
                Console.SetError(savedError);
                Environment.ExitCode = savedExit;
            }
        }
    }

    // ---- Executor and live collections ----

    private readonly List<IAsyncDisposable> _factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static EntityTypeFile BookModel(bool authorSortable) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = Guid.Parse("5f4b0000-0000-0000-0000-00000000b10c"),
            Name = nameof(VlBook),
            ClrType = typeof(VlBook).FullName!,
            IndexName = nameof(VlBooks_ByTitle),
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.Parse("5f4b0000-0000-0000-0000-00000000b10d"), Name = nameof(VlBook.Title), DataType = "string" },
                new EntityAttributeDefinition
                {
                    Id = Guid.Parse("5f4b0000-0000-0000-0000-00000000b10e"),
                    Name = nameof(VlBook.Author),
                    DataType = "string",
                    CanSort = authorSortable ? null : false,
                    CanFilter = authorSortable ? null : false,
                },
            ],
        },
    };

    private static SparkQuery BooksQuery() => new()
    {
        Id = Guid.Parse("5f4b0000-0000-0000-0000-00000000b10f"),
        Name = "GetVlBooks",
        Source = "Database.VlBooks",
        EntityType = nameof(VlBook),
        IndexName = nameof(VlBooks_ByTitle),
        SortColumns = [new SortColumn { Property = nameof(VlBook.Title), Direction = "asc" }],
    };

    private async Task<IQueryExecutor> ExecutorAsync(bool authorSortable)
    {
        var factory = new SparkEndpointFactory<VlBookContext>(Store, [BookModel(authorSortable)],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(VlBooks_ByTitle)));
        _factories.Add(factory);
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new VlBook { Title = "b", Author = "zed" }, "vlbooks/1");
            await session.StoreAsync(new VlBook { Title = "a", Author = "amy" }, "vlbooks/2");
            await session.StoreAsync(new VlBook { Title = "c", Author = "max" }, "vlbooks/3");
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);
        return factory.GetService<IQueryExecutor>();
    }

    [Fact]
    public async Task A_viewless_default_returns_sorted_entity_documents()
    {
        var executor = await ExecutorAsync(authorSortable: false);

        var page = await executor.ExecuteQueryAsync(BooksQuery(), skip: 0, take: 2);
        var descending = await executor.ExecuteQueryAsync(
            BooksQuery().WithSortColumns([new SortColumn { Property = nameof(VlBook.Title), Direction = "desc" }]), skip: 0, take: 50);

        page.Items.Select(i => i.Id).Should().Equal(["vlbooks/2", "vlbooks/1"]);
        page.TotalItems.Should().Be(3);
        page.Items[0].Values.First(v => v.Key == nameof(VlBook.Author)).Value!.ToString()
            .Should().Be("amy", "the rows are entity documents, so a field the map does not emit still has its value");
        descending.Items.Select(i => i.Id).Should().Equal(["vlbooks/3", "vlbooks/1", "vlbooks/2"]);
    }

    [Fact]
    public async Task A_caller_sort_outside_the_emitted_fields_is_refused_instead_of_failing()
    {
        var executor = await ExecutorAsync(authorSortable: false);

        var result = await executor.ExecuteQueryAsync(
            BooksQuery().WithSortColumns([new SortColumn { Property = nameof(VlBook.Author), Direction = "asc" }]), skip: 0, take: 50);

        result.Items.Should().HaveCount(3, "canSort false refuses the sort silently; RavenDB never sees it");
    }

    [Fact]
    public async Task Without_the_minted_flags_the_same_sort_reaches_RavenDB_and_fails()
    {
        // Why the synchronizer mints canSort false outside the surface: SP4's 500.
        var executor = await ExecutorAsync(authorSortable: true);

        var act = () => executor.ExecuteQueryAsync(
            BooksQuery().WithSortColumns([new SortColumn { Property = nameof(VlBook.Author), Direction = "asc" }]), skip: 0, take: 50);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public void A_live_query_on_a_viewless_default_watches_its_own_collection()
    {
        var definition = BookModel(authorSortable: false).PersistentObject;
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypeByName(nameof(VlBook)).Returns(definition);

        var collections = MintPlayer.Spark.Live.LiveQueryCollections.Resolve(BooksQuery(), loader, Catalog(), Store);

        collections.Should().BeEquivalentTo(["VlBooks"]);
    }
}
