using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// The composed virtual PO read path (#324): an Actions class overriding
/// <c>OnComposeAsync</c> serves its type's page from code — no document, no collection guard,
/// no row security — under the type-level "Read" right. The default (no override) leaves the
/// entity pipeline byte-for-byte unchanged, which the ordinary Get tests already pin down.
/// </summary>
public class ComposeEndpointTests : SparkTestDriver
{
    private static readonly Guid StartTypeId = Guid.Parse("22222222-bbbb-bbbb-bbbb-222222222222");
    private static readonly Guid NullComposeTypeId = Guid.Parse("33333333-cccc-cccc-cccc-333333333333");

    private static EntityTypeFile StartModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = StartTypeId,
            Name = "ComposedStart",
            ClrType = typeof(ComposedStart).FullName!,
            Breadcrumb = "{Welcome}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Welcome", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Counter", DataType = "number" },
            ],
        }
    };

    private static EntityTypeFile NullComposeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NullComposeTypeId,
            Name = "ComposedNull",
            ClrType = typeof(ComposedNull).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ],
        }
    };

    private static async Task<Abstractions.PersistentObject?> GetPoAsync(SparkEndpointFactory factory, Guid typeId, string id)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/spark/po/{typeId}/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<Abstractions.PersistentObject>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task Composed_type_serves_the_hook_result_with_the_requested_id_and_no_document()
    {
        await using var factory = new SparkEndpointFactory(Store, [StartModel()]);

        var po = await GetPoAsync(factory, StartTypeId, "start");

        po.Should().NotBeNull("the page is composed — nothing was ever stored");
        po!.Id.Should().Be("start", "the scaffold's id is pre-set to the requested id");
        po.Breadcrumb.Should().Be("Composed for start");
        po.Attributes.Should().Contain(a => a.Name == "Welcome" && a.Value!.ToString() == "Hello from OnComposeAsync");
        po.Etag.Should().BeNull("there is no document, so there is no change vector");
    }

    [Fact]
    public async Task Composed_object_is_forced_read_only_unless_the_hook_says_otherwise()
    {
        await using var factory = new SparkEndpointFactory(Store, [StartModel()]);

        var po = await GetPoAsync(factory, StartTypeId, "whatever");

        po!.Can.Should().NotBeNull("the framework stamps the affordances so the generic UI hides Edit/Delete");
        po.Can!.Edit.Should().BeFalse();
        po.Can.Delete.Should().BeFalse();
    }

    [Fact]
    public async Task Composition_runs_under_the_type_level_Read_right()
    {
        var perms = Substitute.For<IPermissionService>();
        perms.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        perms.EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(x => Task.FromException(new SparkAccessDeniedException($"{x.ArgAt<string>(0)}/{x.ArgAt<string>(1)}")));
        await using var factory = new SparkEndpointFactory(Store, [StartModel()], services =>
        {
            services.RemoveAll<IPermissionService>();
            services.AddSingleton(perms);
        });

        var po = await GetPoAsync(factory, StartTypeId, "start");

        po.Should().BeNull("no Read grant → 404, indistinguishable from missing (M-3)");
    }

    [Fact]
    public async Task Override_returning_null_falls_through_to_the_entity_pipeline()
    {
        await using var factory = new SparkEndpointFactory(Store, [NullComposeModel()]);

        var po = await GetPoAsync(factory, NullComposeTypeId, "composednulls/does-not-exist");

        po.Should().BeNull("null from the hook means 'not composed': the normal load ran and found nothing");
    }
}

/// <summary>Virtual-PO marker: no context root, never stored; see the model file in the test above.</summary>
public sealed class ComposedStart
{
    public string? Welcome { get; set; }
    public int Counter { get; set; }
}

public sealed class ComposedStartActions : DefaultPersistentObjectActions<ComposedStart>
{
    public ComposedStartActions(MintPlayer.Spark.Services.IEntityMapper mapper) : base(mapper) { }

    public override Task<Abstractions.PersistentObject?> OnComposeAsync(SparkComposeArgs args)
    {
        args.PersistentObject["Welcome"].Value = "Hello from OnComposeAsync";
        args.PersistentObject["Counter"].Value = 42;
        args.PersistentObject.Breadcrumb = $"Composed for {args.RequestedId}";
        return Task.FromResult<Abstractions.PersistentObject?>(args.PersistentObject);
    }
}

public sealed class ComposedNull
{
    public string? Name { get; set; }
}

public sealed class ComposedNullActions : DefaultPersistentObjectActions<ComposedNull>
{
    public ComposedNullActions(MintPlayer.Spark.Services.IEntityMapper mapper) : base(mapper) { }

    // An override that decides "not composed" — the entity pipeline must run as if the hook
    // didn't exist.
    public override Task<Abstractions.PersistentObject?> OnComposeAsync(SparkComposeArgs args)
        => Task.FromResult<Abstractions.PersistentObject?>(null);
}
