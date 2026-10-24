using System.Text.Json.Nodes;
using CodeCoverage.Entities;
using CodeCoverage.Tests._Infrastructure;
using Raven.Client.Documents.Operations;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The types that exist only as rows embedded in an owner's document (an account's PR size scale and ignore rules, its
/// dependency rules, a board's event mappings) carry a type-level <c>New</c> grant for every signed-in user, because the
/// nested form needs it. That grant also opens <c>/spark/po/create</c> for the type itself, so without a row check
/// anyone could store such a row outside any account they own. Every standalone create of a valid row is refused, the
/// owner's included, and nothing is stored; the owner edits the rows through the account's save
/// (<see cref="PrSizeLabelSwitchEndToEndTests"/>).
/// </summary>
[Collection(CoverageWebHostCollection.Name)]
public class EmbeddedRowStandaloneWriteTests(OwnerPrivacyHostFixture host) : IClassFixture<OwnerPrivacyHostFixture>
{
    // A valid row per type, so that only the row check can refuse it.
    private static readonly Dictionary<string, (string TypeId, Dictionary<string, object> Values)> Rows = new()
    {
        [nameof(PrSizeLabel)] = ("4453b086-435b-475a-b7b6-db5cc9fd8489",
            new() { ["Name"] = "size/standalone", ["Color"] = "#112233", ["MinLines"] = 1 }),
        [nameof(PrSizeIgnoreRule)] = ("4fba0e9f-e2c9-473d-87ad-69819ef00916",
            new() { ["Glob"] = "**/standalone/**", ["Mode"] = "Ignore" }),
        [nameof(DependencyRule)] = ("6dd82acf-7ee7-4427-b8d0-57710ddb8944",
            new() { ["Match"] = "standalone", ["MaxUpdate"] = "Patch" }),
        [nameof(EventColumnMapping)] = ("f5ddda9f-8c61-43f0-bea5-4b5b34c10e4a", new()),
    };

    public static TheoryData<string, string> Creates()
    {
        var data = new TheoryData<string, string>();
        foreach (var user in new[] { OwnerPrivacyHostFixture.StrangerUserId, OwnerPrivacyHostFixture.OwnerUserId })
            foreach (var typeName in Rows.Keys)
                data.Add(typeName, user);
        return data;
    }

    [Theory]
    [MemberData(nameof(Creates))]
    public async Task A_standalone_row_is_refused_and_nothing_is_stored(string typeName, string user)
    {
        var (objectTypeId, values) = Rows[typeName];
        var (status, body) = await host.PostAsync("/spark/po/new", new { objectTypeId }, user);
        ((int)status).Should().Be(200, body);
        var po = JsonNode.Parse(body)!["result"]!.DeepClone().AsObject();
        foreach (var attribute in po["attributes"]!.AsArray().Select(a => a!.AsObject()).ToList())
        {
            if (!values.TryGetValue(attribute["name"]!.GetValue<string>(), out var value)) continue;
            attribute["value"] = JsonValue.Create(value);
            attribute["isValueChanged"] = true;
        }

        (status, body) = await host.PostRawAsync("/spark/po/create",
            new JsonObject { ["objectTypeId"] = objectTypeId, ["persistentObject"] = po }.ToJsonString(), user);

        // A signed-in caller's row refusal is Spark's uniform "Not found".
        ((int)status).Should().Be(404, body);
        var collection = host.Store.Conventions.FindCollectionName(typeof(PrSizeLabel).Assembly.GetType($"CodeCoverage.Entities.{typeName}")!);
        var stats = await host.Store.Maintenance.SendAsync(new GetCollectionStatisticsOperation());
        stats.Collections.Should().NotContainKey(collection);
    }
}
