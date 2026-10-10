using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Models;
using MintPlayer.Spark.Testing;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests;

/// <summary>
/// The settings are the administrator's alone (<c>docs/seo_analyzer_PRD.md</c> D11, F17): SeoTester's security.json
/// binds <c>settings:administrators</c> to <c>Administrators</c>, a group a user is in through the role on their
/// <c>SparkUser</c>. Any other signed-in user gets the missing-row 404 (#453), on the list and on an edit.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class SettingsAccessTests(SeoTesterHostFixture host)
{
    // The settings package's stamped ids (UUIDv5 over 'settings:…', its App_Data/Model).
    private const string SettingTypeId = "36751d1c-f4e0-578d-a589-9b32db4edc40";
    private const string SettingsQueryId = "9ea40039-d316-5012-8065-eac1f52b21ec";
    private const string Administrators = "Administrators";

    [Fact]
    public async Task An_administrator_lists_every_SeoTester_setting()
    {
        using var client = host.CreateClient();

        var (status, body, raw) = await SeoRequests.PostAsync(client, "/spark/queries/execute", Wire.Query(SettingsQueryId), "SparkUsers/admin", Administrators);

        status.Should().Be(HttpStatusCode.OK, raw);
        var ids = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        ids.Should().Contain(Setting.IdFor(SeoSettings.UnverifiedPagesPerRun.Key));
        ids.Should().Contain(Setting.IdFor(SeoSettings.Sharing.Key));
        ids.Should().Contain(Setting.IdFor(SeoQuotas.Audits.Limit.Key));
    }

    [Fact]
    public async Task A_signed_in_user_who_is_not_an_administrator_neither_lists_nor_edits_a_setting()
    {
        using var client = host.CreateClient();
        var id = Setting.IdFor(SeoSettings.UnverifiedPagesPerRun.Key);

        var (listStatus, _, _) = await SeoRequests.PostAsync(client, "/spark/queries/execute", Wire.Query(SettingsQueryId), "SparkUsers/member");
        var (editStatus, _, _) = await SeoRequests.PostAsync(client, "/spark/po/update", await EditAsync(id, "5000"), "SparkUsers/member");

        listStatus.Should().Be(HttpStatusCode.NotFound, "a refusal reads as a missing row (#453)");
        editStatus.Should().Be(HttpStatusCode.NotFound);
        (await host.Factory.Services.GetRequiredService<ISparkSettings>().GetAsync(SeoSettings.UnverifiedPagesPerRun)).Should().Be(100);
    }

    [Fact]
    public async Task An_anonymous_visitor_cannot_list_the_settings()
    {
        using var client = host.CreateClient();

        var (status, _, _) = await SeoRequests.PostAsync(client, "/spark/queries/execute", Wire.Query(SettingsQueryId), user: null);

        status.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    private async Task<object> EditAsync(string id, string value) => Wire.Typed(SettingTypeId, new
    {
        persistentObject = new
        {
            id,
            etag = await ChangeVectorAsync(id),
            name = "Setting",
            objectTypeId = SettingTypeId,
            attributes = new[] { new { name = "Value", value, isValueChanged = true } },
        },
    }, id);

    /// <summary>The etag a client holding the stored version sends: its change vector.</summary>
    private async Task<string> ChangeVectorAsync(string id)
    {
        using var session = host.Database.OpenAsyncSession();
        var document = await session.LoadAsync<object>(id) ?? throw new InvalidOperationException($"'{id}' was not seeded.");
        return session.Advanced.GetChangeVectorFor(document);
    }
}
