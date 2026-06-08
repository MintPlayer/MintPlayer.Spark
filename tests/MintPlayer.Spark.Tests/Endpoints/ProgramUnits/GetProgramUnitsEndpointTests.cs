using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Endpoints.PersistentObject;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.ProgramUnits;

/// <summary>
/// Endpoint tests for GET /spark/program-units. The endpoint walks the configured
/// program-unit groups, resolves each unit's CLR type via either the model registry
/// (persistentObject id) or the SparkContext property map (Database.* / Custom.*
/// query source), and filters out any unit the current principal can't Query.
///
/// We swap <see cref="IProgramUnitsLoader"/> for an in-memory stub so we don't have to
/// hand-write programUnits.json into each test's content root, and stub
/// <see cref="IPermissionService"/> so we can drive the allow/deny matrix directly.
/// </summary>
public class GetProgramUnitsEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid CompanyTypeId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static readonly Guid PeopleQueryId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid CustomQueryId = Guid.Parse("dddddddd-4444-4444-4444-444444444444");

    private static readonly Guid GroupAId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupBId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private SparkEndpointFactory CreateFactory(
        ProgramUnitsConfiguration config,
        IPermissionService? permissionService = null,
        IQueryLoader? queryLoader = null)
    {
        var perms = permissionService ?? AllowAllPermissions();
        var queries = queryLoader ?? Substitute.For<IQueryLoader>();

        return new SparkEndpointFactory(
            Store,
            [TestModels.Person(PersonTypeId), CompanyModel(CompanyTypeId)],
            services =>
            {
                services.RemoveAll<IProgramUnitsLoader>();
                services.AddSingleton<IProgramUnitsLoader>(new StubProgramUnitsLoader(config));

                services.RemoveAll<IPermissionService>();
                services.AddSingleton(perms);

                services.RemoveAll<IQueryLoader>();
                services.AddSingleton(queries);
            });
    }

    private static IPermissionService AllowAllPermissions()
    {
        var service = Substitute.For<IPermissionService>();
        service.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return service;
    }

    private static async Task<ProgramUnitsConfiguration> GetConfigAsync(SparkEndpointFactory factory)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/spark/program-units");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<ProgramUnitsConfiguration>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new ProgramUnitsConfiguration();
    }

    [Fact]
    public async Task Returns_empty_groups_when_loader_has_no_configuration()
    {
        await using var factory = CreateFactory(new ProgramUnitsConfiguration());

        var config = await GetConfigAsync(factory);

        config.ProgramUnitGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task PersistentObject_unit_is_retained_when_permission_allows()
    {
        var unit = MakePoUnit("People", PersonTypeId);
        var config = OneGroup("People & Companies", unit);
        await using var factory = CreateFactory(config);

        var result = await GetConfigAsync(factory);

        result.ProgramUnitGroups.Should().ContainSingle()
            .Which.ProgramUnits.Should().ContainSingle()
            .Which.Id.Should().Be(unit.Id);
    }

    [Fact]
    public async Task PersistentObject_unit_is_filtered_out_when_permission_denies_query()
    {
        var unit = MakePoUnit("People", PersonTypeId);
        var config = OneGroup("People", unit);
        var perms = Substitute.For<IPermissionService>();
        perms.IsAllowedAsync("Query", "Person", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        await using var factory = CreateFactory(config, permissionService: perms);

        var result = await GetConfigAsync(factory);

        result.ProgramUnitGroups.Should().BeEmpty(
            "the only unit was denied → its group has zero units → group is dropped");
    }

    [Fact]
    public async Task Database_query_unit_resolves_via_SparkContext_property_then_filters_by_permission()
    {
        var query = new SparkQuery
        {
            Id = PeopleQueryId, Name = "AllPeople", Source = "Database.People",
        };
        var queryLoader = Substitute.For<IQueryLoader>();
        queryLoader.GetQuery(PeopleQueryId).Returns(query);

        var unit = MakeQueryUnit("AllPeople", PeopleQueryId);
        var perms = Substitute.For<IPermissionService>();
        // 'People' is the Database.* property name → mapped via TestSparkContext property
        // 'People' (IRavenQueryable<Person>) → entity type 'Person' → permission target.
        perms.IsAllowedAsync("Query", "Person", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        await using var factory = CreateFactory(OneGroup("g", unit), permissionService: perms, queryLoader: queryLoader);

        var result = await GetConfigAsync(factory);

        result.ProgramUnitGroups.Should().ContainSingle()
            .Which.ProgramUnits.Should().ContainSingle()
            .Which.Id.Should().Be(unit.Id);
    }

    [Fact]
    public async Task Custom_query_unit_uses_EntityType_directly_for_permission_target()
    {
        var query = new SparkQuery
        {
            Id = CustomQueryId, Name = "CustomPeople",
            Source = "Custom.SomeMethod",
            EntityType = "Person",
        };
        var queryLoader = Substitute.For<IQueryLoader>();
        queryLoader.GetQuery(CustomQueryId).Returns(query);

        var unit = MakeQueryUnit("Custom", CustomQueryId);
        var perms = Substitute.For<IPermissionService>();
        perms.IsAllowedAsync("Query", "Person", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        await using var factory = CreateFactory(OneGroup("g", unit), permissionService: perms, queryLoader: queryLoader);

        var result = await GetConfigAsync(factory);

        result.ProgramUnitGroups.Should().ContainSingle()
            .Which.ProgramUnits.Should().ContainSingle()
            .Which.Id.Should().Be(unit.Id);
    }

    [Fact]
    public async Task Custom_query_unit_without_EntityType_falls_through_to_unrestricted_keep()
    {
        // Source starts with Custom., EntityType empty → ResolveClrType returns null →
        // the endpoint keeps the unit (fail-open: any unit whose target can't be resolved
        // is shown rather than hidden).
        var query = new SparkQuery
        {
            Id = CustomQueryId, Name = "CustomBare", Source = "Custom.Bare", EntityType = null,
        };
        var queryLoader = Substitute.For<IQueryLoader>();
        queryLoader.GetQuery(CustomQueryId).Returns(query);

        var unit = MakeQueryUnit("Bare", CustomQueryId);
        await using var factory = CreateFactory(OneGroup("g", unit), queryLoader: queryLoader);

        var result = await GetConfigAsync(factory);

        result.ProgramUnitGroups.Should().ContainSingle()
            .Which.ProgramUnits.Should().ContainSingle();
    }

    [Fact]
    public async Task Group_with_all_units_filtered_out_is_dropped_entirely()
    {
        var allowed = MakePoUnit("Companies", CompanyTypeId);
        var denied = MakePoUnit("People", PersonTypeId);
        var config = new ProgramUnitsConfiguration
        {
            ProgramUnitGroups =
            [
                new ProgramUnitGroup
                {
                    Id = GroupAId, Name = TranslatedString.Create("CompaniesOnly"), Order = 0,
                    ProgramUnits = [allowed],
                },
                new ProgramUnitGroup
                {
                    Id = GroupBId, Name = TranslatedString.Create("PeopleOnly"), Order = 1,
                    ProgramUnits = [denied],
                },
            ],
        };
        var perms = Substitute.For<IPermissionService>();
        perms.IsAllowedAsync("Query", "Company", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        perms.IsAllowedAsync("Query", "Person", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        await using var factory = CreateFactory(config, permissionService: perms);

        var result = await GetConfigAsync(factory);

        result.ProgramUnitGroups.Should().ContainSingle()
            .Which.Id.Should().Be(GroupAId);
    }

    [Fact]
    public async Task Unknown_unit_type_is_kept_unrestricted()
    {
        // Type is neither persistentObject nor query → ResolveClrType returns null → unit kept.
        var unit = new ProgramUnit
        {
            Id = Guid.NewGuid(),
            Name = TranslatedString.Create("UnknownType"),
            Type = "external-link",
        };
        await using var factory = CreateFactory(OneGroup("g", unit));

        var result = await GetConfigAsync(factory);

        result.ProgramUnitGroups.Should().ContainSingle()
            .Which.ProgramUnits.Should().ContainSingle()
            .Which.Type.Should().Be("external-link");
    }

    // ---- helpers --------------------------------------------------------

    private static ProgramUnitsConfiguration OneGroup(string name, params ProgramUnit[] units) => new()
    {
        ProgramUnitGroups =
        [
            new ProgramUnitGroup
            {
                Id = GroupAId,
                Name = TranslatedString.Create(name),
                Order = 0,
                ProgramUnits = units,
            }
        ],
    };

    private static ProgramUnit MakePoUnit(string name, Guid persistentObjectId) => new()
    {
        Id = Guid.NewGuid(),
        Name = TranslatedString.Create(name),
        Type = "persistentObject",
        PersistentObjectId = persistentObjectId,
    };

    private static ProgramUnit MakeQueryUnit(string name, Guid queryId) => new()
    {
        Id = Guid.NewGuid(),
        Name = TranslatedString.Create(name),
        Type = "query",
        QueryId = queryId,
    };

    private static EntityTypeFile CompanyModel(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "Company",
            ClrType = typeof(Company).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ],
        }
    };

    private sealed class StubProgramUnitsLoader : IProgramUnitsLoader
    {
        private readonly ProgramUnitsConfiguration _config;
        public StubProgramUnitsLoader(ProgramUnitsConfiguration config) => _config = config;
        public ProgramUnitsConfiguration GetProgramUnits() => _config;
    }
}
