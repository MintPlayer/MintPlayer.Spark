using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// The composed model (composition M4, D5, D6): library model layers, then the application's files;
/// an application file for a library type is a delta; library ids are UUIDv5 and immutable.
/// </summary>
public class SparkModelLayersTests : IDisposable
{
    private const string SparkUserId = "0d3faefa-624c-5135-bca7-652aa9053e0e";
    private const string UserNameId = "78698642-85ae-5c15-bcd7-8885dc64c60b";

    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "spark-model-layers-" + Guid.NewGuid().ToString("N"));

    public SparkModelLayersTests() => Directory.CreateDirectory(ModelDir);

    public void Dispose()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelDir => Path.Combine(contentRoot, "App_Data", "Model");

    private void AppFile(string name, string json) => File.WriteAllText(Path.Combine(ModelDir, name), json);

    private static SparkLibrary Library(string assembly, string alias, string json, params string[] dependsOn)
        => new(alias, assembly, dependsOn, [new SparkLibraryLayer("model", "Model/SparkUser.json", json)]);

    private static readonly string LibrarySparkUser = $$"""
        {
          "_comment": "shipped by the library",
          "persistentObject": {
            "id": "{{SparkUserId}}",
            "name": "SparkUser",
            "clrType": "MintPlayer.Spark.Authorization.Identity.SparkUser",
            "alias": "sparkuser",
            "breadcrumb": "{UserName}",
            "attributes": [
              { "id": "{{UserNameId}}", "name": "UserName", "dataType": "string", "isReadOnly": true, "order": 1, "showedOn": "Query, PersistentObject", "rules": [] }
            ],
            "queries": []
          },
          "queries": []
        }
        """;

    private static readonly SparkLibrary Authorization = Library("MintPlayer.Spark.Authorization", "authorization", LibrarySparkUser);

    private const string QnADelta = """
        { "$extends": "authorization:SparkUser", "persistentObject": { "name": "SparkUser", "attributes": [ { "name": "UserName", "showedOn": "PersistentObject" } ] } }
        """;

    private const string Car = """
        { "persistentObject": { "id": "27768be5-2ff5-4782-8b22-c0e8d163050e", "name": "Car", "attributes": [], "queries": ["GetCarParts"] } }
        """;

    private static JsonObject Po(SparkComposedType type) => (JsonObject)JsonNode.Parse(type.Json)!["persistentObject"]!;

    // ── UUIDv5 (D5) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Uuid_v5_matches_the_rfc_4122_example()
        => SparkModelIds.Create(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com")
            .Should().Be(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"));

    [Fact]
    public void Library_ids_derive_from_the_alias_and_names_in_the_fixed_namespace()
    {
        SparkModelIds.Namespace.Should().Be(Guid.Parse("036a66ee-1790-46de-9f0e-4e1d856750c9"), "changing it changes every shipped id");
        SparkModelIds.For("authorization:SparkUser").Should().Be(Guid.Parse(SparkUserId));
        SparkModelIds.For("authorization:SparkUser.attributes.UserName").Should().Be(Guid.Parse(UserNameId));
    }

    [Fact]
    public void Verification_names_every_missing_or_wrong_id_with_the_expected_value()
    {
        var root = (SparkJsonObject)SparkJson.Parse("""
            { "persistentObject": { "id": "3f7c1d92-8e4a-4b16-9c05-2a7d6e0f4b83", "name": "SparkUser",
              "attributes": [ { "name": "UserName" }, { "id": "{{UserNameId}}", "name": "Email" } ],
              "tabs": [ { "id": "{{tab}}", "name": "Main" } ] },
              "queries": [ { "name": "GetUsers" } ] }
            """.Replace("{{UserNameId}}", UserNameId).Replace("{{tab}}", SparkModelIds.For("authorization:SparkUser.tabs.Main").ToString()));

        var problems = SparkModelIds.Verify("authorization", root);

        problems.Select(p => p.Path).Should().Equal(
            "persistentObject.id",
            "persistentObject.attributes[UserName].id",
            "persistentObject.attributes[Email].id",
            "queries[GetUsers].id");
        problems[0].Expected.Should().Be(SparkUserId);
        problems[0].Actual.Should().Be("3f7c1d92-8e4a-4b16-9c05-2a7d6e0f4b83");
        problems[1].Actual.Should().BeNull();
        problems[1].Seed.Should().Be("authorization:SparkUser.attributes.UserName");
        problems[3].Seed.Should().Be("authorization:SparkUser.queries.GetUsers");
    }

    [Fact]
    public void The_shipped_SparkUser_model_carries_its_derived_ids()
    {
        var path = Path.Combine(RepositoryRoot(), "libs", "authorization", "MintPlayer.Spark.Authorization", "App_Data", "Model", "SparkUser.json");
        var root = (SparkJsonObject)SparkJson.Parse(File.ReadAllText(path));

        SparkModelIds.Verify("authorization", root).Should().BeEmpty();
    }

    // ── Composition ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_library_type_is_in_the_model_without_an_application_file()
    {
        AppFile("Car.json", Car);

        var types = SparkModelFiles.Compose([Authorization], ModelDir);

        types.Select(t => t.Name).Should().Equal("SparkUser", "Car");
        var sparkUser = types[0];
        sparkUser.Library.Should().Be("MintPlayer.Spark.Authorization");
        sparkUser.AppFile.Should().BeNull();
        sparkUser.FileName.Should().Be("SparkUser.json");
        Po(sparkUser)["id"]!.GetValue<string>().Should().Be(SparkUserId);
        sparkUser.Json.Should().NotContain("_comment", "annotations are not data");
        types[1].Library.Should().BeNull();
        types[1].AppFile.Should().Be(Path.Combine(ModelDir, "Car.json"));
    }

    [Fact]
    public void An_application_delta_composes_onto_the_library_type_with_provenance()
    {
        AppFile("SparkUser.json", QnADelta);

        var sparkUser = SparkModelFiles.Compose([Authorization], ModelDir).Should().ContainSingle().Which;

        var attribute = (JsonObject)Po(sparkUser)["attributes"]![0]!;
        attribute["showedOn"]!.GetValue<string>().Should().Be("PersistentObject");
        attribute["isReadOnly"]!.GetValue<bool>().Should().BeTrue();
        attribute["id"]!.GetValue<string>().Should().Be(UserNameId);
        sparkUser.AppFile.Should().Be(Path.Combine(ModelDir, "SparkUser.json"));
        sparkUser.Layers.Should().Equal("MintPlayer.Spark.Authorization", Path.Combine(ModelDir, "SparkUser.json"));
        sparkUser.Provenance["persistentObject.attributes[UserName].showedOn"].Should().Be(Path.Combine(ModelDir, "SparkUser.json"));
        sparkUser.Provenance["persistentObject.attributes[UserName].isReadOnly"].Should().Be("MintPlayer.Spark.Authorization");
        sparkUser.Source.Should().Be("SparkUser (MintPlayer.Spark.Authorization + SparkUser.json)");
    }

    [Fact]
    public void An_application_file_that_changes_a_library_id_is_refused()
    {
        AppFile("SparkUser.json", """{ "$extends": "authorization:SparkUser", "persistentObject": { "id": "4e13c0de-0000-4000-8000-000000000001", "name": "SparkUser" } }""");

        var act = () => SparkModelFiles.Compose([Authorization], ModelDir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*persistentObject.id*cannot be changed*$extends*");
    }

    [Fact]
    public void Two_unrelated_libraries_shipping_one_name_are_refused_with_the_rename_remedy_and_a_dependent_one_may_override()
    {
        var other = Library("Other.Lib", "other", LibrarySparkUser.Replace("\"order\": 1", "\"order\": 5"));
        var dependent = Library("Other.Lib", "other", LibrarySparkUser.Replace("\"order\": 1", "\"order\": 5"), "MintPlayer.Spark.Authorization");

        var conflicting = () => SparkModelFiles.Compose([Authorization, other], ModelDir);
        conflicting.Should().Throw<InvalidOperationException>()
            .WithMessage("*MintPlayer.Spark.Authorization*Other.Lib*SparkUser*SPARK057*PersistentObjectName(\"other\", \"SparkUser\"*");

        var type = SparkModelFiles.Compose([Authorization, dependent], ModelDir).Should().ContainSingle().Which;
        Po(type)["attributes"]![0]!["order"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public void Application_files_that_do_not_compose_pass_through_as_written()
    {
        AppFile("Broken.json", """{ "persistentObject": { "name": "Broken", } }""");
        AppFile("Car.json", Car);

        var types = SparkModelFiles.Compose([], ModelDir);

        types.Should().HaveCount(2);
        types.Single(t => t.FileName == "Broken.json").Json.Should().Be("""{ "persistentObject": { "name": "Broken", } }""");
        types.Single(t => t.FileName == "Broken.json").Name.Should().BeNull();
        Po(types.Single(t => t.Name == "Car"))["queries"]![0]!.GetValue<string>()
            .Should().Be("GetCarParts", "sub-queries are aliases, not named elements, and are replaced whole");
    }

    [Fact]
    public void Two_application_files_for_one_name_stay_two_types()
    {
        AppFile("A.json", Car);
        AppFile("B.json", Car.Replace("27768be5", "37768be5"));

        SparkModelFiles.Compose([], ModelDir).Should().HaveCount(2);
    }

    // ── Name identity (D28): $extends, F7, F8, F9 ──────────────────────────────────────────────

    [Fact]
    public void An_application_type_named_like_a_library_type_is_refused_instead_of_becoming_its_delta()
    {
        // F7 (red before the fix): an application's own SparkUser (its own id and class) joined the library type by
        // name, so the library's rights covered the application's documents while its row policies did not.
        AppFile("SparkUser.json", """
            { "persistentObject": { "id": "4e13c0de-0000-4000-8000-000000000001", "name": "SparkUser", "clrType": "QnA.Entities.SparkUser", "attributes": [] } }
            """);

        var act = () => SparkModelFiles.Compose([Authorization], ModelDir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*names 'SparkUser'*authorization:SparkUser*without \"$extends\"*PersistentObjectName(\"authorization\", \"SparkUser\"*");
    }

    [Fact]
    public void The_refusal_never_advises_removing_an_own_types_id()
    {
        // F8 (red before the fix): the message advised "state only what differs, and never an id" for every
        // problem, which turned an application's own colliding type into a delta on the library's (F7).
        AppFile("SparkUser.json", """{ "persistentObject": { "id": "4e13c0de-0000-4000-8000-000000000001", "name": "SparkUser" } }""");

        var act = () => SparkModelFiles.Compose([Authorization], ModelDir);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("\"$extends\"").And.Contain("PersistentObjectName").And.Contain("Never remove an own type's id");
    }

    [Fact]
    public void An_orphaned_delta_is_refused_instead_of_skipped()
    {
        // F9 (red before the fix): the loader skipped the file with a console line and served the library type
        // without the delta's restrictions.
        AppFile("Gone.json", """{ "$extends": "authorization:Gone", "persistentObject": { "name": "Gone", "showedOn": "None" } }""");

        var act = () => SparkModelFiles.Compose([Authorization], ModelDir);

        act.Should().Throw<InvalidOperationException>().WithMessage("*extends 'authorization:Gone'*orphaned delta*");
    }

    [Fact]
    public void An_id_less_file_that_extends_nothing_is_refused()
    {
        // F9: the shape a delta is left in once its library type is gone or renamed and it never stated $extends.
        AppFile("SparkUser.json", """{ "persistentObject": { "name": "FormerlySparkUser", "attributes": [ { "name": "UserName", "showedOn": "None" } ] } }""");

        var act = () => SparkModelFiles.Compose([Authorization], ModelDir);

        act.Should().Throw<InvalidOperationException>().WithMessage("*states no persistentObject.id*");
    }

    [Fact]
    public void A_stale_delta_names_the_type_it_extends_by_its_old_name_and_is_refused()
    {
        var renamed = SparkLayerCatalog.Rename([Authorization], [new PersistentObjectNameAttribute("authorization", "SparkUser", "Member")]);
        AppFile("SparkUser.json", QnADelta);

        var act = () => SparkModelFiles.Compose(renamed, ModelDir);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'SparkUser'*named 'Member'*stale delta*--spark-synchronize-model*");
    }

    [Fact]
    public void A_delta_cannot_change_the_types_clr_class()
    {
        AppFile("SparkUser.json", """{ "$extends": "authorization:SparkUser", "persistentObject": { "name": "SparkUser", "clrType": "QnA.Entities.SparkUser" } }""");

        var act = () => SparkModelFiles.Compose([Authorization], ModelDir);

        act.Should().Throw<InvalidOperationException>().WithMessage("*maps 'QnA.Entities.SparkUser'*CLR class*");
    }

    [Fact]
    public void A_renamed_library_type_frees_its_old_name_for_an_application_type_and_keeps_its_id()
    {
        var renamed = SparkLayerCatalog.Rename([Authorization], [new PersistentObjectNameAttribute("authorization", "SparkUser", "Member")]);
        AppFile("Member.json", QnADelta.Replace("\"name\": \"SparkUser\"", "\"name\": \"Member\""));
        AppFile("SparkUser.json", """{ "persistentObject": { "id": "4e13c0de-0000-4000-8000-000000000001", "name": "SparkUser", "clrType": "QnA.Entities.SparkUser", "attributes": [] } }""");

        var types = SparkModelFiles.Compose(renamed, ModelDir);

        var member = types.Single(t => t.Name == "Member");
        member.Identity.Should().Be("authorization:SparkUser");
        member.DeclaredName.Should().Be("SparkUser");
        Po(member)["id"]!.GetValue<string>().Should().Be(SparkUserId, "a library id derives from alias:DeclaredName, which a rename leaves alone");
        Po(member)["attributes"]![0]!["showedOn"]!.GetValue<string>().Should().Be("PersistentObject", "the delta applies under the new name");
        types.Single(t => t.Name == "SparkUser").Library.Should().BeNull("the old name is the application's own type now");
    }

    [Fact]
    public void A_rename_rewrites_the_librarys_rights_queries_and_translations_and_keeps_the_shipped_text()
    {
        var withQuery = (JsonObject)JsonNode.Parse(LibrarySparkUser)!;
        withQuery["queries"] = JsonNode.Parse("""[ { "id": "aaaaaaaa-0000-4000-8000-000000000002", "name": "GetUsers", "entityType": "SparkUser", "source": "Database.Users" } ]""");
        var library = new SparkLibrary("authorization", "MintPlayer.Spark.Authorization", [],
        [
            new SparkLibraryLayer("model", "Model/SparkUser.json", withQuery.ToJsonString()),
            new SparkLibraryLayer("security", "security.json", """{ "rights": [ { "key": "read", "resource": "QueryRead/SparkUser", "groupId": "@authenticated" }, { "key": "name", "resource": "Edit/SparkUser/UserName", "groupId": "@authenticated" } ] }"""),
            new SparkLibraryLayer("translations", "translations.json", """{ "model": { "SparkUser": { "label": { "en": "User" } } }, "queries": { "GetUsers": { "label": { "en": "Users" } } } }"""),
        ]);

        var renamed = SparkLayerCatalog.Rename([library], [new PersistentObjectNameAttribute("authorization", "SparkUser", "Member")]).Single();

        var model = renamed.Layers.Single(l => l.Kind == "model");
        var po = JsonNode.Parse(model.Json)!;
        po["persistentObject"]!["name"]!.GetValue<string>().Should().Be("Member");
        po["queries"]![0]!["entityType"]!.GetValue<string>().Should().Be("Member");
        model.Shipped.Should().Be(library.Layers[0].Json, "provenance hashes are over what the library ships");
        renamed.Layers.Single(l => l.Kind == "security").Json.Should().Contain("QueryRead/Member").And.Contain("Edit/Member/UserName").And.NotContain("/SparkUser");
        var translations = renamed.Layers.Single(l => l.Kind == "translations").Json;
        translations.Should().Contain("model.Member.label").And.NotContain("model.SparkUser").And.Contain("queries.GetUsers.label");
        renamed.Renames.Should().Equal(new SparkTypeRename("SparkUser", "Member"));
    }

    [Fact]
    public void A_rename_changes_no_layer_hash()
    {
        AppFile("SparkUser.json", QnADelta);
        var before = ModelFileShape.ComputeLayerHashes([Authorization], ModelDir);
        File.Delete(Path.Combine(ModelDir, "SparkUser.json"));
        AppFile("Member.json", QnADelta.Replace("\"name\": \"SparkUser\"", "\"name\": \"Member\""));
        var renamed = SparkLayerCatalog.Rename([Authorization], [new PersistentObjectNameAttribute("authorization", "SparkUser", "Member")]);

        var after = ModelFileShape.ComputeLayerHashes(renamed, ModelDir);

        after["Model/SparkUser.json"]["authorization"].Should().Be(before["Model/SparkUser.json"]["authorization"],
            "the library layer is hashed as shipped; the rename is reported by the names map instead");
    }

    [Theory]
    [InlineData("authorization", "Nope", "Member", "*ships no persistent-object type named 'Nope'*")]
    [InlineData("nolib", "SparkUser", "Member", "*no referenced library has the alias 'nolib'*")]
    [InlineData("authorization", "SparkUser", "Mem.ber", "*not a valid persistent-object name*")]
    public void A_rename_that_cannot_be_honoured_refuses_startup(string library, string declared, string name, string message)
    {
        var act = () => SparkLayerCatalog.Rename([Authorization], [new PersistentObjectNameAttribute(library, declared, name)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SPARK061*" + message);
    }

    [Fact]
    public void A_rename_onto_another_types_name_is_refused_ignoring_case()
    {
        var other = new SparkLibrary("other", "Other.Lib", [], [new SparkLibraryLayer("model", "Model/Member.json",
            """{ "persistentObject": { "id": "bbbbbbbb-0000-4000-8000-000000000001", "name": "Member", "attributes": [] } }""")]);

        var act = () => SparkLayerCatalog.Rename([Authorization, other], [new PersistentObjectNameAttribute("authorization", "SparkUser", "member")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'member' is already the name of*other:Member*");
    }

    // ── Hashes and the server's loader ─────────────────────────────────────────────────────────

    [Fact]
    public void A_type_moved_into_a_library_keeps_its_hash_key_and_value()
    {
        var copy = LibrarySparkUser
            .Replace(SparkUserId, "3f7c1d92-8e4a-4b16-9c05-2a7d6e0f4b83")
            .Replace(UserNameId, "7d4a2c68-1f05-4e93-b72a-6c8d0e5f3a19");
        AppFile("SparkUser.json", copy);
        var before = ModelFileShape.ComputeFileHashes([], ModelDir);
        File.Delete(Path.Combine(ModelDir, "SparkUser.json"));

        var after = ModelFileShape.ComputeFileHashes([Authorization], ModelDir);

        after.Should().ContainKey("SparkUser.json");
        after["SparkUser.json"].Should().Be(before["SparkUser.json"], "ids are not structural, so moving the type changes no hash");
    }

    [Fact]
    public void The_loader_serves_a_library_type_with_the_application_delta()
    {
        AppFile("SparkUser.json", QnADelta);
        AppFile("Car.json", Car);
        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.ContentRootPath.Returns(contentRoot);

        var loader = new ModelLoader(ModelSource.For(hostEnvironment, [Authorization]), TranslationsLoader.For(hostEnvironment, []), NullLogger<ModelLoader>.Instance);

        var sparkUser = loader.GetEntityType(Guid.Parse(SparkUserId));
        sparkUser.Should().NotBeNull();
        sparkUser!.Attributes.Should().ContainSingle().Which.ShowedOn.ToString().Should().Be("PersistentObject");
        loader.ResolveEntityType("sparkuser").Should().BeSameAs(sparkUser);
        loader.GetEntityTypeByName("Car").Should().NotBeNull();
    }

    // ── Delta (the writers' inverse of Compose) ────────────────────────────────────────────────

    [Fact]
    public void The_delta_states_only_what_differs_and_composes_back_to_the_desired_model()
    {
        var library = (SparkJsonObject)SparkJson.Parse(LibrarySparkUser);
        var desired = (SparkJsonObject)SparkJson.Parse(LibrarySparkUser
            .Replace("\"Query, PersistentObject\"", "\"PersistentObject\"")
            .Replace("\"rules\": [] }", "\"rules\": [] }, { \"id\": \"aaaaaaaa-0000-4000-8000-000000000001\", \"name\": \"DisplayName\", \"dataType\": \"string\" }"));

        var delta = SparkModelLayers.Delta(library, desired);

        SparkJson.Write(delta!).Should().Be(
            """{"persistentObject":{"name":"SparkUser","attributes":[{"name":"UserName","showedOn":"PersistentObject"},{"id":"aaaaaaaa-0000-4000-8000-000000000001","name":"DisplayName","dataType":"string"}]}}""");
        var recomposed = SparkLayers.Compose(
            [SparkLayer.Parse("lib", LibrarySparkUser, isLibrary: true), new SparkLayer("app", delta, isLibrary: false)],
            SparkKinds.Model);
        recomposed.Errors.Should().BeEmpty();
        SparkJsonNode.DeepEquals(recomposed.Result, SparkLayers.Compose([new SparkLayer("d", desired, isLibrary: false)], SparkKinds.Model).Result)
            .Should().BeTrue();
    }

    [Fact]
    public void Nothing_differing_is_no_delta_and_a_member_left_out_is_not_a_removal()
    {
        var library = (SparkJsonObject)SparkJson.Parse(LibrarySparkUser);
        var desired = (SparkJsonObject)SparkJson.Parse("""{ "persistentObject": { "name": "SparkUser", "breadcrumb": "{UserName}" } }""");

        SparkModelLayers.Delta(library, library).Should().BeNull();
        SparkModelLayers.Delta(library, desired).Should().BeNull("the writer never deletes (#253)");
    }

    [Fact]
    public void The_delta_never_restates_a_library_id()
    {
        var library = (SparkJsonObject)SparkJson.Parse(LibrarySparkUser);
        var desired = (SparkJsonObject)SparkJson.Parse(LibrarySparkUser.Replace(SparkUserId, "3f7c1d92-8e4a-4b16-9c05-2a7d6e0f4b83"));

        SparkModelLayers.Delta(library, desired).Should().BeNull();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
