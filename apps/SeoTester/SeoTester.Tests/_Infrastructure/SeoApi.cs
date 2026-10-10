using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using SeoTester.Entities;

namespace SeoTester.Tests._Infrastructure;

/// <summary>Requests SeoTester's own types, as the SPA makes them, plus seeding shortcuts for the shared host.</summary>
internal static class SeoApi
{
    // The model's ids (App_Data/Model/*.json).
    public const string SiteTypeId = "36c1bfcf-bb87-4a14-ba10-0a4f0551dd9d";
    public const string AuditRunTypeId = "023f697e-698d-41ca-9f45-5003a937a50d";
    public const string CheckDefinitionTypeId = "b474d13d-5d55-4abb-806f-bfd72d7382de";
    public const string AuditLogEntryTypeId = "6b1e34fe-b6a2-4838-a153-9929abc030c8";
    public const string SitesQueryId = "e9fe78ac-680a-4a2e-b27b-fd3d46966092";
    public const string CheckDefinitionsQueryId = "8d1be047-0014-4221-bd04-e5c7eb94332f";
    public const string AuditLogQueryId = "2498a0df-9613-4822-8b12-b60a8449c83f";

    public const string Ann = "SparkUsers/ann", Max = "SparkUsers/max", Vic = "SparkUsers/vic", Bob = "SparkUsers/bob";
    public const string Acme = "Organizations/acme", Globex = "Organizations/globex";

    public static Task<(HttpStatusCode Status, JsonElement Body, string Raw)> CreateSiteAsync(SeoTesterHostFixture host, string origin, string? organizationId, string user, params (string Name, object? Value)[] extra)
    {
        var attributes = new List<object> { new { name = "Origin", value = origin, isValueChanged = true } };
        if (organizationId is not null)
            attributes.Add(new { name = "OrganizationId", value = organizationId, isValueChanged = true });
        attributes.AddRange(extra.Select(e => (object)new { name = e.Name, value = e.Value, isValueChanged = true }));
        return PostAsync(host, "/spark/po/create", Wire.Typed(SiteTypeId, new
        {
            persistentObject = new { name = "Site", objectTypeId = SiteTypeId, attributes },
        }), user);
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> UpdateAsync(SeoTesterHostFixture host, string typeId, string typeName, string id, string user, params (string Name, object? Value)[] values)
        => await PostAsync(host, "/spark/po/update", Wire.Typed(typeId, new
        {
            persistentObject = new
            {
                id,
                etag = await EtagAsync(host, id),
                name = typeName,
                objectTypeId = typeId,
                attributes = values.Select(v => new { name = v.Name, value = v.Value, isValueChanged = true }).ToArray(),
            },
        }, id), user);

    public static Task<(HttpStatusCode Status, JsonElement Body, string Raw)> LoadAsync(SeoTesterHostFixture host, string typeId, string id, string? user, params string[] roles)
        => PostAsync(host, "/spark/po/load", Wire.Typed(typeId, id: id), user, roles);

    public static Task<(HttpStatusCode Status, JsonElement Body, string Raw)> ActionAsync(SeoTesterHostFixture host, string typeId, string typeName, string action, string id, string user)
        => PostAsync(host, "/spark/actions/execute", Wire.Action(typeId, action, new { parent = new { id, objectTypeId = typeId, name = typeName } }), user);

    public static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> PostAsync(SeoTesterHostFixture host, string url, object payload, string? user, params string[] roles)
    {
        using var client = host.CreateClient();
        return await SeoRequests.PostAsync(client, url, payload, user, roles);
    }

    public static async Task<string> EtagAsync(SeoTesterHostFixture host, string id)
    {
        using var session = host.Database.OpenAsyncSession();
        var document = await session.LoadAsync<object>(id) ?? throw new InvalidOperationException($"'{id}' is not stored.");
        return session.Advanced.GetChangeVectorFor(document);
    }

    /// <summary>A site stored directly (no pipeline), with a fresh token and a unique host under example.com.</summary>
    public static async Task<Site> SeedSiteAsync(SeoTesterHostFixture host, string organizationId, bool verified = false, string? hostName = null)
    {
        hostName ??= $"t{Guid.NewGuid():N}"[..13] + ".example.com";
        var site = new Site
        {
            OrganizationId = organizationId,
            Origin = "https://" + hostName,
            Host = hostName,
            VerificationToken = SeoTester.Sites.OwnershipProof.NewToken(),
            IsVerified = verified,
            VerificationMethod = verified ? "Dns" : null,
            VerifiedAtUtc = verified ? DateTime.UtcNow : null,
            VerificationCheckedAtUtc = verified ? DateTime.UtcNow : null,
        };
        using var session = host.Database.OpenAsyncSession();
        await session.StoreAsync(site);
        await session.SaveChangesAsync();
        return site;
    }

    public static async Task<T?> LoadDocumentAsync<T>(SeoTesterHostFixture host, string id)
    {
        using var session = host.Database.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    /// <summary>Stores an administrator's value for a setting and drops the settings cache, as the Settings screen does.</summary>
    public static async Task SetSettingAsync(SeoTesterHostFixture host, string key, string? value)
    {
        using (var session = host.Database.OpenAsyncSession())
        {
            var setting = await session.LoadAsync<Setting>(Setting.IdFor(key)) ?? new Setting { Id = Setting.IdFor(key), Key = key };
            setting.Value = value;
            await session.StoreAsync(setting);
            await session.SaveChangesAsync();
        }
        host.Factory.Services.GetRequiredService<ISparkSettings>().Invalidate();
    }
}
