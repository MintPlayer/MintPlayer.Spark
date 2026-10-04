using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.Migrations;
using MintPlayer.Spark.Testing;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// #388 R4: SparkUser and SparkRole moved from MintPlayer.Spark.Authorization to
/// MintPlayer.Spark.Authorization.Abstractions. Stored documents record the old assembly in
/// <c>@metadata.Raven-Clr-Type</c>, which no longer resolves, so an untyped load (row security,
/// breadcrumbs) does not materialize them as their type. <see cref="M_202610041800_UserDocumentsMovedAssembly"/>
/// rewrites exactly that value.
/// </summary>
public class UserDocumentsMovedAssemblyMigrationTests : SparkTestDriver
{
    private const string OldUserType = "MintPlayer.Spark.Authorization.Identity.SparkUser, MintPlayer.Spark.Authorization";
    private const string OldRoleType = "MintPlayer.Spark.Authorization.Identity.SparkRole, MintPlayer.Spark.Authorization";
    private const string SubclassType = "TestApp.AppUser, TestApp";

    [Fact]
    public async Task Users_and_roles_recorded_under_the_old_assembly_load_as_their_type_afterwards()
    {
        await SeedAsync();

        // Red: the recorded type no longer resolves, so an untyped load is not a SparkUser.
        (await LoadUntypedAsync("SparkUsers/1")).Should().NotBeOfType<SparkUser>();

        await new M_202610041800_UserDocumentsMovedAssembly(Store).UpAsync(CancellationToken.None);

        (await LoadUntypedAsync("SparkUsers/1")).Should().BeOfType<SparkUser>();
        (await LoadUntypedAsync("SparkRoles/1")).Should().BeOfType<SparkRole>();
        (await ClrTypeAsync("SparkUsers/1")).Should().Be(Store.Conventions.FindClrTypeName(typeof(SparkUser)));
        (await ClrTypeAsync("SparkRoles/1")).Should().Be(Store.Conventions.FindClrTypeName(typeof(SparkRole)));
    }

    [Fact]
    public async Task An_application_subclass_is_left_alone_and_a_second_run_changes_nothing()
    {
        await SeedAsync();
        var migration = new M_202610041800_UserDocumentsMovedAssembly(Store);

        await migration.UpAsync(CancellationToken.None);
        var afterFirst = await ChangeVectorsAsync();
        await migration.UpAsync(CancellationToken.None);

        var afterSecond = await ChangeVectorsAsync();
        foreach (var (id, changeVector) in afterFirst)
            afterSecond[id].Should().Be(changeVector, $"a rerun must not rewrite {id}");
        (await ClrTypeAsync("SparkUsers/2")).Should().Be(SubclassType);
    }

    private async Task SeedAsync()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new SparkUser { UserName = "old" }, "SparkUsers/1");
            await session.StoreAsync(new SparkUser { UserName = "subclass" }, "SparkUsers/2");
            await session.StoreAsync(new SparkRole { Name = "old" }, "SparkRoles/1");
            await session.SaveChangesAsync();
        }

        // Write the metadata a pre-#388 deployment left behind.
        await SetClrTypeAsync("SparkUsers/1", OldUserType);
        await SetClrTypeAsync("SparkUsers/2", SubclassType);
        await SetClrTypeAsync("SparkRoles/1", OldRoleType);
    }

    private async Task SetClrTypeAsync(string id, string clrType)
    {
        var operation = await Store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from @all_docs where id() = $id update { this['@metadata']['Raven-Clr-Type'] = $type; }",
            QueryParameters = new Parameters { ["id"] = id, ["type"] = clrType },
        }));
        await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
    }

    private async Task<object?> LoadUntypedAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<object>(id);
    }

    private async Task<string?> ClrTypeAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var document = await session.LoadAsync<object>(id);
        return session.Advanced.GetMetadataFor(document).TryGetValue(Constants.Documents.Metadata.RavenClrType, out string? value) ? value : null;
    }

    private async Task<Dictionary<string, string?>> ChangeVectorsAsync()
    {
        var result = new Dictionary<string, string?>();
        foreach (var id in new[] { "SparkUsers/1", "SparkUsers/2", "SparkRoles/1" })
        {
            using var session = Store.OpenAsyncSession();
            var document = await session.LoadAsync<object>(id);
            result[id] = session.Advanced.GetChangeVectorFor(document);
        }
        return result;
    }
}
