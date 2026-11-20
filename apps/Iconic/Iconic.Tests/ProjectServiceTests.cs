using System.Text;
using System.Text.Json;
using Iconic.Entities;
using Iconic.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Iconic.Tests;

/// <summary>
/// The editor's API at the service level (docs/iconic_PRD.md Stage 0): ownership, the scene patch script (S5), version
/// conflicts, quotas (D21), snapshots (D22) and share links (D20), against a real RavenDB.
/// </summary>
public sealed class ProjectServiceTests : SparkTestDriver
{
    private sealed class User(string? id, params string[] groups) : ISparkCurrentUser, IGroupMembershipProvider
    {
        public string? Id { get; set; } = id;
        public bool IsAuthenticated => Id is not null;
        public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>(groups);
    }

    private ServiceProvider Services(User user, Action<IconicQuotas>? quotas = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Store);
        services.AddScoped(_ => Store.OpenAsyncSession());
        services.AddSingleton<ISparkCurrentUser>(user);
        services.AddSingleton<IGroupMembershipProvider>(user);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<IconicQuotas>(q => quotas?.Invoke(q));
        services.AddScoped<IconicAccess>();
        services.AddScoped<ProjectService>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> Call<T>(ServiceProvider sp, Func<ProjectService, Task<T>> call)
    {
        using var scope = sp.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<ProjectService>());
    }

    private static int Status(IResult result) => result switch
    {
        IStatusCodeHttpResult { StatusCode: { } code } => code,
        _ => 200,
    };

    private static JsonElement Body(IResult result)
        => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<string> CreateAsync(ServiceProvider sp, string scene = """{"points":{},"layers":[]}""")
    {
        var result = await Call(sp, s => s.CreateAsync(new CreateProjectRequest("Logo", ProjectKind.Icon, 256, 256, Json(scene)), default));
        Status(result).Should().Be(200);
        return Body(result).GetProperty("key").GetString()!;
    }

    [Fact]
    public void The_kind_is_read_by_name_as_the_client_sends_it()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonSerializer.Deserialize<CreateProjectRequest>("""{"name":"x","kind":"Cursor"}""", web)!.Kind.Should().Be(ProjectKind.Cursor);
        JsonSerializer.Deserialize<UpdateProjectRequest>("""{"kind":null}""", web)!.Kind.HasValue.Should().BeFalse();
    }

    private async Task<ProjectScene> SceneAsync(string key)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<ProjectScene>($"ProjectScenes/{key}"))!;
    }

    [Fact]
    public async Task Create_stores_the_project_and_its_scene_for_the_owner()
    {
        var sp = Services(new User("users/1"));
        var key = await CreateAsync(sp);

        using var session = Store.OpenAsyncSession();
        var project = await session.LoadAsync<Project>($"Projects/{key}");
        project!.OwnerId.Should().Be("users/1");
        project.Name.Should().Be("Logo");
        (await SceneAsync(key)).Version.Should().Be(1);
    }

    [Fact]
    public async Task The_list_holds_only_the_callers_projects()
    {
        var mine = Services(new User("users/1"));
        var theirs = Services(new User("users/2"));
        var a = await CreateAsync(mine);
        var b = await CreateAsync(mine);
        await CreateAsync(theirs);
        WaitForIndexing(Store);

        var body = Body(await Call(mine, s => s.ListAsync(default)));
        body.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("key").GetString())
            .Should().BeEquivalentTo([a, b]);
        Status(await Call(Services(new User(null)), s => s.ListAsync(default))).Should().Be(401);
    }

    [Fact]
    public async Task Anonymous_callers_cannot_create()
    {
        var result = await Call(Services(new User(null)), s => s.CreateAsync(new CreateProjectRequest(null, null, null, null, null), default));
        Status(result).Should().Be(401);
    }

    [Fact]
    public async Task Another_users_project_answers_404()
    {
        var key = await CreateAsync(Services(new User("users/1")));
        var result = await Call(Services(new User("users/2")), s => s.GetAsync(key, default));
        Status(result).Should().Be(404);
    }

    [Fact]
    public async Task Only_the_owner_deletes_a_project_with_its_scene()
    {
        var owner = Services(new User("users/1"));
        var key = await CreateAsync(owner);

        Status(await Call(Services(new User("users/2")), s => s.DeleteAsync(key, default))).Should().Be(404);
        Status(await Call(owner, s => s.DeleteAsync(key, default))).Should().Be(204);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Project>($"Projects/{key}")).Should().BeNull();
        (await session.LoadAsync<ProjectScene>($"ProjectScenes/{key}")).Should().BeNull();
    }

    [Fact]
    public async Task Patch_sets_and_removes_values_and_raises_the_version()
    {
        var sp = Services(new User("users/1"));
        var key = await CreateAsync(sp, """{"points":{"a":{"x":1,"y":2},"b":{"x":3,"y":4}},"layers":[{"id":"l1"},{"id":"l2"}]}""");

        var ops = new List<SceneOp>
        {
            new("set", ["points", "a", "x"], Json("10")),
            new("remove", ["points", "b"], null),
            new("set", ["shapes", "s1", "style"], Json("""{"stroke":"#000"}""")),
            new("remove", ["layers", "0"], null),
        };
        var result = await Call(sp, s => s.PatchSceneAsync(key, new PatchSceneRequest(1, ops), default));
        Status(result).Should().Be(200);
        Body(result).GetProperty("version").GetInt64().Should().Be(2);

        var scene = await SceneAsync(key);
        var json = Json(scene.Json);
        json.GetProperty("points").GetProperty("a").GetProperty("x").GetInt32().Should().Be(10);
        json.GetProperty("points").TryGetProperty("b", out _).Should().BeFalse();
        json.GetProperty("shapes").GetProperty("s1").GetProperty("style").GetProperty("stroke").GetString().Should().Be("#000");
        json.GetProperty("layers").GetArrayLength().Should().Be(1);
        json.GetProperty("layers")[0].GetProperty("id").GetString().Should().Be("l2");
        scene.Bytes.Should().Be(scene.Json.Length);
    }

    [Fact]
    public async Task Patch_from_a_stale_version_is_a_409_and_changes_nothing()
    {
        var sp = Services(new User("users/1"));
        var key = await CreateAsync(sp, """{"n":1}""");
        var stale = await Call(sp, s => s.PatchSceneAsync(key, new PatchSceneRequest(7, [new("set", ["n"], Json("2"))]), default));

        Status(stale).Should().Be(409);
        Body(stale).GetProperty("version").GetInt64().Should().Be(1);
        (await SceneAsync(key)).Json.Should().Be("""{"n":1}""");
    }

    [Theory]
    [InlineData("__proto__")]
    [InlineData("constructor")]
    [InlineData("prototype")]
    public async Task Patch_refuses_prototype_paths(string segment)
    {
        var sp = Services(new User("users/1"));
        var key = await CreateAsync(sp);
        var result = await Call(sp, s => s.PatchSceneAsync(key, new PatchSceneRequest(1, [new("set", ["points", segment, "x"], Json("1"))]), default));
        Status(result).Should().Be(400);
    }

    [Fact]
    public async Task A_patch_that_outgrows_the_scene_quota_is_a_413_and_changes_nothing()
    {
        var sp = Services(new User("users/1"), q => q.MaxSceneBytes = 200);
        var key = await CreateAsync(sp, """{"n":1}""");
        var big = new string('x', 300);
        var result = await Call(sp, s => s.PatchSceneAsync(key, new PatchSceneRequest(1, [new("set", ["blob"], Json($"\"{big}\""))]), default));

        Status(result).Should().Be(413);
        var scene = await SceneAsync(key);
        scene.Version.Should().Be(1);
        scene.Json.Should().Be("""{"n":1}""");
    }

    [Fact]
    public async Task The_project_quota_refuses_one_more()
    {
        var sp = Services(new User("users/1"), q => q.MaxProjects = 2);
        await CreateAsync(sp);
        await CreateAsync(sp);
        WaitForIndexing(Store);
        var third = await Call(sp, s => s.CreateAsync(new CreateProjectRequest("x", null, null, null, null), default));
        Status(third).Should().Be(409);
    }

    [Fact]
    public async Task Full_save_replaces_the_scene_and_checks_the_version()
    {
        var sp = Services(new User("users/1"));
        var key = await CreateAsync(sp);
        var ok = await Call(sp, s => s.SaveSceneAsync(key, new SaveSceneRequest(1, Json("""{"v":2}""")), default));
        Status(ok).Should().Be(200);
        var stale = await Call(sp, s => s.SaveSceneAsync(key, new SaveSceneRequest(1, Json("""{"v":3}""")), default));
        Status(stale).Should().Be(409);
        (await SceneAsync(key)).Json.Should().Be("""{"v":2}""");
    }

    [Fact]
    public async Task Restoring_a_snapshot_keeps_the_current_scene_as_a_snapshot()
    {
        var sp = Services(new User("users/1"));
        var key = await CreateAsync(sp, """{"v":1}""");
        Status(await Call(sp, s => s.CreateSnapshotAsync(key, new SnapshotRequest("first"), default))).Should().Be(200);
        Status(await Call(sp, s => s.SaveSceneAsync(key, new SaveSceneRequest(1, Json("""{"v":2}""")), default))).Should().Be(200);

        string snapshotId;
        using (var session = Store.OpenAsyncSession())
            snapshotId = (await session.LoadAsync<Project>($"Projects/{key}"))!.Snapshots.Single().Id;

        var restored = await Call(sp, s => s.RestoreSnapshotAsync(key, snapshotId, default));
        Status(restored).Should().Be(200);
        (await SceneAsync(key)).Json.Should().Be("""{"v":1}""");

        using var check = Store.OpenAsyncSession();
        var project = (await check.LoadAsync<Project>($"Projects/{key}"))!;
        project.Snapshots.Count.Should().Be(2);
        var before = project.Snapshots.Single(s => s.Id != snapshotId);
        var attachment = await check.Advanced.Attachments.GetAsync(project, $"snapshots/{before.Id}.json");
        using var reader = new StreamReader(attachment.Stream, Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Be("""{"v":2}""");
    }

    [Fact]
    public async Task The_snapshot_limit_refuses_a_restore_that_would_exceed_it()
    {
        var sp = Services(new User("users/1"), q => q.MaxSnapshots = 1);
        var key = await CreateAsync(sp);
        Status(await Call(sp, s => s.CreateSnapshotAsync(key, new SnapshotRequest("only"), default))).Should().Be(200);
        Status(await Call(sp, s => s.CreateSnapshotAsync(key, new SnapshotRequest("one more"), default))).Should().Be(409);
    }

    [Fact]
    public async Task Attachments_check_the_name_the_content_and_the_quota()
    {
        var sp = Services(new User("users/1"), q => q.MaxAttachmentBytes = 64);
        var key = await CreateAsync(sp);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

        Status(await Call(sp, s => s.PutFileAsync(key, "paint/layer1/32.png", Request(png), default))).Should().Be(200);
        Status(await Call(sp, s => s.PutFileAsync(key, "../evil.png", Request(png), default))).Should().Be(400);
        Status(await Call(sp, s => s.PutFileAsync(key, "thumbnail.png", Request("GIF89a.."u8.ToArray()), default))).Should().Be(400);
        Status(await Call(sp, s => s.PutFileAsync(key, "paint/layer1/64.png", Request([.. png, .. new byte[60]]), default))).Should().Be(413);

        var file = await Call(sp, s => s.GetFileAsync(key, "paint/layer1/32.png", default));
        ((FileContentHttpResult)file).FileContents.ToArray().Should().Equal(png);
    }

    private static HttpRequest Request(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        return context.Request;
    }

    [Fact]
    public async Task A_share_link_reads_the_drawing_until_it_is_taken_down()
    {
        var owner = Services(new User("users/1"));
        var key = await CreateAsync(owner, """{"v":1}""");
        var share = await Call(owner, s => s.ShareAsync(key, new ShareRequest(true, false), default));
        var token = Body(share).GetProperty("shareToken").GetString()!;
        WaitForIndexing(Store);

        var anonymous = Services(new User(null));
        var read = await Call(anonymous, s => s.GetSharedAsync(token, default));
        Status(read).Should().Be(200);
        Body(read).GetProperty("scene").GetProperty("v").GetInt32().Should().Be(1);

        // Only an administrator takes a link down.
        Status(await Call(Services(new User("users/2")), s => s.TakedownAsync(new TakedownRequest(token), default))).Should().Be(403);
        Status(await Call(Services(new User("users/3", IconicAccess.AdministratorGroup)), s => s.TakedownAsync(new TakedownRequest(token), default))).Should().Be(200);
        WaitForIndexing(Store);

        Status(await Call(anonymous, s => s.GetSharedAsync(token, default))).Should().Be(404);
        Status(await Call(owner, s => s.ShareAsync(key, new ShareRequest(true, true), default))).Should().Be(409);
    }

    [Fact]
    public async Task Remix_copies_the_scene_and_the_shared_files_to_the_caller()
    {
        var owner = Services(new User("users/1"));
        var key = await CreateAsync(owner, """{"v":1}""");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9];
        Status(await Call(owner, s => s.PutFileAsync(key, "paint/l/16.png", Request(png), default))).Should().Be(200);
        Status(await Call(owner, s => s.CreateSnapshotAsync(key, new SnapshotRequest("private"), default))).Should().Be(200);
        var token = Body(await Call(owner, s => s.ShareAsync(key, new ShareRequest(true, false), default))).GetProperty("shareToken").GetString()!;
        WaitForIndexing(Store);

        var other = Services(new User("users/2"));
        var remix = await Call(other, s => s.RemixAsync(token, default));
        Status(remix).Should().Be(200);
        var copyKey = Body(remix).GetProperty("key").GetString()!;

        (await SceneAsync(copyKey)).Json.Should().Be("""{"v":1}""");
        using var session = Store.OpenAsyncSession();
        var copy = (await session.LoadAsync<Project>($"Projects/{copyKey}"))!;
        copy.OwnerId.Should().Be("users/2");
        session.Advanced.Attachments.GetNames(copy).Select(a => a.Name).Should().Equal(["paint/l/16.png"]);
    }
}
