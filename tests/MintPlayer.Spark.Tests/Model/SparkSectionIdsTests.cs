using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// #721 (<c>docs/section_fragment_links_PRD.md</c>, Dec1, Dec2, Dec7): every group, tab and sub-query of an object page has a
/// section id, computed once on the server. A group's or tab's is its name in kebab case, a sub-query's is its query's alias.
/// The ids of one type are unique, match <c>^[a-z][a-z0-9]*(-[a-z0-9]+)*$</c> and never start with <c>spark-</c> or
/// <c>bs-</c>; a model that breaks this fails in <c>--spark-verify-model</c>, at startup and in the synchronizer.
/// </summary>
public sealed class SparkSectionIdsTests : IDisposable
{
    // ---- the kebab rule (Part B) ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("General", "general")]
    [InlineData("MergeQueue", "merge-queue")]
    [InlineData("DependencyUpdates", "dependency-updates")]
    [InlineData("PrSizeLabels", "pr-size-labels")]
    [InlineData("URLPath", "url-path")]
    [InlineData("IOStream", "io-stream")]
    [InlineData("MyURL", "my-url")]
    [InlineData("Step2Details", "step2-details")]
    [InlineData("HTML5Parser", "html5-parser")]
    [InlineData("merge-queue", "merge-queue")]
    [InlineData("Merge-Queue", "merge-queue")]
    [InlineData("Spark", "spark")]
    public void A_name_becomes_its_kebab_case_anchor(string name, string expected)
        => SparkSectionIds.Anchor(name).Should().Be(expected);

    [Theory]
    [InlineData("Merge Queue")]
    [InlineData("Merge_Queue")]
    [InlineData("Élan")]
    [InlineData("2Step")]
    [InlineData("Trailing-")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_that_is_no_identifier_gives_no_anchor(string? name)
        => SparkSectionIds.Anchor(name).Should().BeNull("an odd name is refused, never stripped (Dec2)");

    [Theory]
    [InlineData("SparkMain")]
    [InlineData("BsTabControl")]
    [InlineData("spark-actions")]
    public void An_anchor_with_a_reserved_prefix_is_refused(string name)
        => SparkSectionIds.Anchor(name).Should().BeNull("spark- and bs- belong to the shell and the tab control");

    // ---- the validator (Part G) -----------------------------------------------------------------------------------------

    private static EntityTypeDefinition Type(string name, string[]? groups = null, string[]? tabs = null, SparkSubQuery[]? queries = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ClrType = $"App.{name}",
        Groups = [.. (groups ?? []).Select((g, i) => new AttributeGroup { Id = Guid.NewGuid(), Name = g, Order = i })],
        Tabs = [.. (tabs ?? []).Select((t, i) => new AttributeTab { Id = Guid.NewGuid(), Name = t, Order = i })],
        Queries = queries ?? [],
    };

    private static SparkQuery Query(string name, string? alias, string entityType = "Person") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Alias = alias,
        Source = $"Custom.{name}",
        EntityType = entityType,
    };

    [Fact]
    public void A_model_with_distinct_valid_ids_has_no_problems()
    {
        var people = Query("Company_People", "company-people");
        var company = Type("Company", groups: ["Identity", "MergeQueue"], tabs: ["General", "Employment"], queries: ["company-people"]);

        SparkSectionIds.Validate([company], [people]).Should().BeEmpty();
    }

    [Fact]
    public void A_group_and_a_tab_with_the_same_id_are_refused()
        => SparkSectionIds.Validate([Type("Person", groups: ["Career"], tabs: ["Career"])], [])
            .Should().ContainSingle().Which.Should().Contain("'Person'").And.Contain("'career'").And.Contain("group 'Career'").And.Contain("tab 'Career'");

    [Fact]
    public void A_group_and_a_sub_query_with_the_same_id_are_refused()
    {
        var repositories = Query("Account_Repositories", "repositories", entityType: "Repository");
        var account = Type("Account", groups: ["Repositories"], queries: ["repositories"]);

        SparkSectionIds.Validate([account], [repositories])
            .Should().ContainSingle().Which.Should().Contain("group 'Repositories'").And.Contain("sub-query 'repositories'");
    }

    [Fact]
    public void Two_groups_whose_names_differ_only_in_spelling_are_refused()
        => SparkSectionIds.Validate([Type("Account", groups: ["MergeQueue", "merge-queue"])], [])
            .Should().ContainSingle().Which.Should().Contain("'merge-queue'");

    [Fact]
    public void A_group_whose_name_gives_no_id_is_refused()
        => SparkSectionIds.Validate([Type("Account", groups: ["Merge Queue"])], [])
            .Should().ContainSingle().Which.Should().Contain("group 'Merge Queue'").And.Contain("no valid section id");

    [Fact]
    public void A_tab_with_a_reserved_prefix_is_refused()
        => SparkSectionIds.Validate([Type("Account", tabs: ["SparkMain"])], [])
            .Should().ContainSingle().Which.Should().Contain("tab 'SparkMain'").And.Contain("spark-");

    [Fact]
    public void A_sub_query_alias_that_is_no_id_is_refused()
    {
        var people = Query("Company_People", "Company_People");
        SparkSectionIds.Validate([Type("Company", queries: ["Company_People"])], [people])
            .Should().ContainSingle().Which.Should().Contain("sub-query 'Company_People'");
    }

    [Fact]
    public void An_id_form_sub_query_takes_its_query_alias()
    {
        var people = Query("Company_People", "people");
        var company = Type("Company", groups: ["People"], queries: [people.Id.ToString()]);

        SparkSectionIds.SubQueryAnchor(company.Queries[0], [people]).Should().Be("people");
        SparkSectionIds.Validate([company], [people])
            .Should().ContainSingle("the id resolves to the alias 'people', which the group already gives")
            .Which.Should().Contain("'people'");
    }

    [Fact]
    public void A_sub_query_without_an_alias_takes_the_derived_one()
    {
        var cars = Query("GetCars", alias: null);
        SparkSectionIds.SubQueryAnchor(new SparkSubQuery { Query = cars.Id.ToString() }, [cars]).Should().Be("cars");
    }

    [Fact]
    public void An_unresolvable_sub_query_is_left_to_the_pruner()
        => SparkSectionIds.Validate([Type("Company", queries: ["nothing-here", "Not An Alias"])], []).Should().BeEmpty();

    [Fact]
    public void Startup_throws_naming_every_problem()
    {
        var act = () => SparkSectionIds.ThrowIfInvalid([Type("Person", groups: ["Career", "Merge Queue"], tabs: ["Career"])], []);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("'career'");
        message.Should().Contain("'Merge Queue'");
    }

    // ---- served, never written (Dec1) -----------------------------------------------------------------------------------

    [Fact]
    public void The_served_type_carries_an_anchor_on_each_group_and_tab()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        SparkPresentation.Install(options);
        var type = Type("Account", groups: ["PrSizeLabels", "Merge Queue"], tabs: ["Employment"]);

        var json = JsonNode.Parse(JsonSerializer.Serialize(type, options))!;

        json["groups"]![0]!["anchor"]!.GetValue<string>().Should().Be("pr-size-labels");
        json["groups"]![1]!.AsObject().ContainsKey("anchor").Should().BeFalse("a refused name serves no anchor");
        json["tabs"]![0]!["anchor"]!.GetValue<string>().Should().Be("employment");
    }

    [Fact]
    public void The_synchronizer_options_never_write_an_anchor()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(Type("Account", groups: ["PrSizeLabels"], tabs: ["Employment"]), options);

        json.Should().NotContain("anchor");
    }

    [Fact]
    public async Task The_catalogue_serves_an_id_form_sub_query_as_its_alias()
    {
        var people = Query("Company_People", "company-people");
        var company = Type("Company", queries: [new SparkSubQuery { Query = people.Id.ToString(), SelectionMode = SparkSelectionMode.None }]);
        var queryLoader = Substitute.For<IQueryLoader>();
        queryLoader.ResolveQuery(people.Id.ToString()).Returns(people);
        var permissions = Substitute.For<IPermissionService>();
        permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var served = await SubQueryPruner.PruneAsync(company, queryLoader, permissions, NullLogger.Instance);

        served.Should().NotBeSameAs(company);
        served.Queries.Should().ContainSingle();
        served.Queries[0].Query.Should().Be("company-people");
        served.Queries[0].SelectionMode.Should().Be(SparkSelectionMode.None);
        company.Queries[0].Query.Should().Be(people.Id.ToString(), "the loader's shared definition is never changed");
    }

    [Fact]
    public async Task The_catalogue_keeps_the_same_reference_when_every_entry_is_already_an_alias()
    {
        var people = Query("Company_People", "company-people");
        var company = Type("Company", queries: ["company-people"]);
        var queryLoader = Substitute.For<IQueryLoader>();
        queryLoader.ResolveQuery("company-people").Returns(people);
        var permissions = Substitute.For<IPermissionService>();
        permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        (await SubQueryPruner.PruneAsync(company, queryLoader, permissions, NullLogger.Instance)).Should().BeSameAs(company);
    }

    // ---- the synchronizer (Dec7) ----------------------------------------------------------------------------------------

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "spark-section-ids-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog _indexCatalog = Substitute.For<IIndexCatalog>();

    public SparkSectionIdsTests()
    {
        _hostEnv.ContentRootPath.Returns(_tempDir);
        _indexCatalog.GetAllEntries().Returns([]);
        _indexCatalog.GetDefaultFor(Arg.Any<Type>()).Returns((IndexCatalogEntry?)null);
        _indexCatalog.GetByIndexName(Arg.Any<string>()).Returns((IndexCatalogEntry?)null);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void The_synchronizer_refuses_two_sections_with_one_id_and_leaves_the_file_alone()
    {
        new ModelSynchronizer(_hostEnv, _indexCatalog).SynchronizeModels(typeof(SecContext));
        var file = Path.Combine(_tempDir, "App_Data", "Model", "SecPerson.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["persistentObject"]!["tabs"] = new JsonArray(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Career" });
        json["persistentObject"]!["groups"] = new JsonArray(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Career" });
        File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var before = File.ReadAllText(file);

        var act = () => new ModelSynchronizer(_hostEnv, _indexCatalog).SynchronizeModels(typeof(SecContext));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("'career'").And.Contain("SecPerson");
        File.ReadAllText(file).Should().Be(before);
    }
}

public class SecPerson
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class SecContext : SparkContext
{
    public IQueryable<SecPerson> People => Session.Query<SecPerson>();
}
