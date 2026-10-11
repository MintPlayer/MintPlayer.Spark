using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Models;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Settings;

/// <summary>
/// Spike S11 / X9 (<c>docs/seo_analyzer_PRD.md</c>) through the real route table: the library's
/// <c>QueryReadEdit/Setting</c> right reaches exactly the group the application binds to
/// <c>settings:administrators</c>; New and Delete exist for nobody; a save is validated against the
/// declaration; the saving instance sees the change at once; and an application that does not bind the
/// slot does not start.
/// </summary>
public class SettingsEndpointTests : SparkTestDriver
{
    private const string SettingTypeId = "36751d1c-f4e0-578d-a589-9b32db4edc40";
    private const string SettingsQueryId = "9ea40039-d316-5012-8065-eac1f52b21ec";
    private static readonly Guid Admins = Guid.Parse("5e771000-0000-4000-8000-000000000001");
    private static readonly Guid Editors = Guid.Parse("5e771000-0000-4000-8000-000000000002");
    private const string AdminsName = "StAdmins";
    private const string EditorsName = "StEditors";

    private static readonly string MaxPagesId = Setting.IdFor(SparkSettingsTests.StSettings.MaxPages.Key);

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static SecurityConfiguration Security(bool bindSlot = true)
    {
        var libraries = SparkTestSecurity.LibraryRightsOff() ?? [];
        libraries.Remove("settings");
        return new SecurityConfiguration
        {
            WellKnown = new Dictionary<string, string>
            {
                [SparkWellKnownGroups.Anonymous] = SparkTestSecurity.AnonymousGroupId.ToString(),
                [SparkWellKnownGroups.Authenticated] = SparkTestSecurity.AuthenticatedGroupId.ToString(),
            },
            Groups = new Dictionary<string, string>
            {
                [SparkTestSecurity.AnonymousGroupId.ToString()] = "Anonymous visitors",
                [SparkTestSecurity.AuthenticatedGroupId.ToString()] = "Signed-in users",
                [Admins.ToString()] = AdminsName,
                [Editors.ToString()] = EditorsName,
            },
            Bindings = bindSlot ? new Dictionary<string, List<string>> { ["settings:administrators"] = [AdminsName] } : null,
            Libraries = libraries.Count > 0 ? libraries : null,
        };
    }

    private SparkEndpointFactory<StContext> Factory(bool bindSlot = true)
    {
        var factory = new SparkEndpointFactory<StContext>(
            Store,
            [],
            configureServices: services => services.AddSingleton<IStartupFilter, StPrincipalStartupFilter>(),
            configureSpark: spark => spark.AddSettings(typeof(SparkSettingsTests.StSettings)),
            security: SparkTestSecurity.FromJson(JsonSerializer.Serialize(Security(bindSlot))));
        factories.Add(factory);
        return factory;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(SparkEndpointFactory<StContext> factory, string url, object payload, string? user, params string[] groups)
    {
        using var client = factory.CreateClient();
        var (cookie, xsrf) = await MintAsync(client, user, groups);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        AddIdentity(request, user, groups);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static void AddIdentity(HttpRequestMessage request, string? user, string[] groups)
    {
        if (user is not null)
            request.Headers.Add("X-St-User", user);
        if (groups.Length > 0)
            request.Headers.Add("X-St-Groups", string.Join(',', groups));
    }

    /// <summary>An antiforgery pair minted for this identity: the token is bound to the user name.</summary>
    private static async Task<(string Cookie, string Xsrf)> MintAsync(HttpClient client, string? user, string[] groups)
    {
        var warmup = new HttpRequestMessage(HttpMethod.Get, "/spark");
        AddIdentity(warmup, user, groups);
        var response = await client.SendAsync(warmup);
        string? antiforgery = null, xsrf = null;
        foreach (var raw in response.Headers.GetValues("Set-Cookie"))
        {
            var nameValue = raw.Split(';', 2)[0];
            var eq = nameValue.IndexOf('=');
            if (eq < 0) continue;
            if (nameValue.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
                antiforgery = nameValue;
            else if (nameValue[..eq] == "XSRF-TOKEN")
                xsrf = Uri.UnescapeDataString(nameValue[(eq + 1)..]);
        }
        return (antiforgery + "; XSRF-TOKEN=" + Uri.EscapeDataString(xsrf!), xsrf!);
    }

    private async Task<object> EditAsync(string id, string? value) => Wire.Typed(SettingTypeId, new
    {
        persistentObject = new
        {
            id,
            etag = await StoredEtag.OfAsync(Store, id),
            name = "Setting",
            objectTypeId = SettingTypeId,
            attributes = new[] { new { name = "Value", value, isValueChanged = true } },
        },
    }, id);

    private async Task<Setting> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<Setting>(id);
    }

    // ---- rights ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_bound_group_lists_and_edits_a_setting_and_the_saving_instance_sees_it_at_once()
    {
        var factory = Factory();
        var settings = factory.GetService<ISparkSettings>();
        (await settings.GetAsync(SparkSettingsTests.StSettings.MaxPages)).Should().Be(5, "seeded at startup with no value");

        var (listStatus, list) = await SendAsync(factory, "/spark/queries/execute", Wire.Query(SettingsQueryId), "users/admin", AdminsName);
        listStatus.Should().Be(HttpStatusCode.OK);
        list.GetProperty("items").GetArrayLength().Should().Be(3, "the seeder wrote one document per declared key");

        var (status, _) = await SendAsync(factory, "/spark/po/update", await EditAsync(MaxPagesId, "25"), "users/admin", AdminsName);

        status.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(MaxPagesId)).Value.Should().Be("25");
        (await settings.GetAsync(SparkSettingsTests.StSettings.MaxPages)).Should().Be(25, "the after-save interceptor dropped this instance's snapshot");
    }

    [Fact]
    public async Task A_signed_in_user_outside_the_bound_group_may_not_edit()
    {
        var factory = Factory();

        var (status, _) = await SendAsync(factory, "/spark/po/update", await EditAsync(MaxPagesId, "25"), "users/editor", EditorsName);
        var (listStatus, _) = await SendAsync(factory, "/spark/queries/execute", Wire.Query(SettingsQueryId), "users/editor", EditorsName);

        // A refusal is indistinguishable from a missing row (M-3, DenyAllEndpointMirrorTests): 404, not 403.
        // A refusal is indistinguishable from a missing row (M-3, DenyAllEndpointMirrorTests): 404, not
        // 403. The member's 200 on the same routes is the other half of the evidence.
        status.Should().Be(HttpStatusCode.NotFound);
        listStatus.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync(MaxPagesId)).Value.Should().BeNull();
    }

    [Fact]
    public async Task Nobody_creates_or_deletes_a_setting_not_even_the_bound_group()
    {
        var factory = Factory();

        var (createStatus, _) = await SendAsync(factory, "/spark/po/create", Wire.Typed(SettingTypeId, new
        {
            persistentObject = new
            {
                name = "Setting",
                objectTypeId = SettingTypeId,
                attributes = new[] { new { name = "Value", value = "1", isValueChanged = true } },
            },
        }), "users/admin", AdminsName);
        var (deleteStatus, _) = await SendAsync(factory, "/spark/po/delete",
            Wire.Typed(SettingTypeId, id: MaxPagesId, etag: await StoredEtag.OfAsync(Store, MaxPagesId)), "users/admin", AdminsName);

        createStatus.Should().Be(HttpStatusCode.NotFound, "refused like a missing type (M-3)");
        deleteStatus.Should().Be(HttpStatusCode.NotFound, "refused like a missing row (M-3)");
        (await LoadAsync(MaxPagesId)).Should().NotBeNull();
    }

    [Fact]
    public void An_application_that_does_not_bind_the_slot_does_not_start()
    {
        var act = () => Factory(bindSlot: false);

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("settings:administrators");
    }

    // ---- validation ------------------------------------------------------------------------------

    [Fact]
    public async Task A_value_that_does_not_parse_as_the_declared_type_is_refused()
    {
        var factory = Factory();

        var (status, body) = await SendAsync(factory, "/spark/po/update", await EditAsync(MaxPagesId, "lots"), "users/admin", AdminsName);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetRawText().Should().Contain("Int32");
        (await LoadAsync(MaxPagesId)).Value.Should().BeNull();
    }

    [Fact]
    public async Task A_setting_whose_key_is_no_longer_declared_is_refused()
    {
        var factory = Factory();
        const string oldId = "Settings/Seo:Removed";
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Setting { Id = oldId, Key = "Seo:Removed", Type = "Int32" }, oldId);
            await session.SaveChangesAsync();
        }

        var (status, body) = await SendAsync(factory, "/spark/po/update", await EditAsync(oldId, "3"), "users/admin", AdminsName);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetRawText().Should().Contain("not a declared setting");
        (await LoadAsync(oldId)).Value.Should().BeNull();
    }

    [Fact]
    public async Task Clearing_the_value_stores_not_overridden()
    {
        var factory = Factory();
        await SendAsync(factory, "/spark/po/update", await EditAsync(MaxPagesId, "25"), "users/admin", AdminsName);

        var (status, _) = await SendAsync(factory, "/spark/po/update", await EditAsync(MaxPagesId, "  "), "users/admin", AdminsName);

        status.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(MaxPagesId)).Value.Should().BeNull();
        (await factory.GetService<ISparkSettings>().GetAsync(SparkSettingsTests.StSettings.MaxPages)).Should().Be(5);
    }
}

public class StContext : SparkContext
{
}

/// <summary>Sets the request's principal from <c>X-St-User</c> / <c>X-St-Groups</c>: a signed-in user without Identity.</summary>
internal sealed class StPrincipalStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue("X-St-User", out var user) && user.ToString() is { Length: > 0 } id)
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, id) };
                if (context.Request.Headers.TryGetValue("X-St-Groups", out var groups))
                    claims.AddRange(groups.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(g => new Claim("group", g)));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "StTest"));
            }
            await nextMiddleware();
        });
        next(app);
    };
}
