using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// What of <c>programUnits.json</c> the model hash covers (owner, 2026-10-15): the <c>url</c> units' addresses,
/// keyed by the unit's id, and nothing else. A tampered external link must refuse startup; renaming, reordering or
/// regrouping the menu must not need a synchronize run.
/// </summary>
public sealed class ProgramUnitsUrlHashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spark-unit-hash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Menu = """
        {
          "_comment": ["The menu."],
          "programUnitGroups": [
            {
              "id": "11111111-1111-1111-1111-111111111111", "name": "programUnits.groups.main", "icon": "bi-house", "order": 1,
              "programUnits": [
                { "id": "22222222-2222-2222-2222-222222222222", "name": "programUnits.cars", "icon": "bi-car-front",
                  "type": "query", "queryId": "33333333-3333-3333-3333-333333333333", "order": 1 },
                { "id": "44444444-4444-4444-4444-444444444444", "name": "programUnits.setup", "icon": "bi-signpost-split",
                  "type": "url", "url": "/merge-queue/setup", "order": 2 },
                { "id": "55555555-5555-5555-5555-555555555555", "name": "programUnits.docs",
                  "type": "url", "url": "https://example.com/docs", "order": 3 }
              ]
            },
            { "id": "66666666-6666-6666-6666-666666666666", "name": "programUnits.groups.other", "order": 2, "programUnits": [] }
          ]
        }
        """;

    /// <summary>The menu's hash, or <c>(absent)</c> when the file has no entry.</summary>
    private string Hash(string programUnits)
    {
        var appData = Path.Combine(_root, Guid.NewGuid().ToString("N"), "App_Data");
        Directory.CreateDirectory(appData);
        File.WriteAllText(Path.Combine(appData, ConfigFileShape.ProgramUnitsFileName), programUnits);
        return ConfigFileShape.ComputeFileHashes(appData).GetValueOrDefault(ConfigFileShape.ProgramUnitsFileName) ?? "(absent)";
    }

    private static string Edit(string from, string to)
    {
        Menu.Should().Contain(from, "the edit must apply, or the assertion proves nothing");
        return Menu.Replace(from, to);
    }

    [Theory]
    [InlineData("\"url\": \"/merge-queue/setup\"", "\"url\": \"/merge-queue/setup?provider=github\"")]
    [InlineData("\"url\": \"https://example.com/docs\"", "\"url\": \"https://evil.example/docs\"")]
    // The loader reads property names ignoring case, so a "URL" carries an address past a hash that reads only "url".
    [InlineData("\"url\": \"https://example.com/docs\"", "\"URL\": \"https://evil.example/docs\"")]
    // A query unit turned into a url unit is a new external link.
    [InlineData("\"type\": \"query\", \"queryId\": \"33333333-3333-3333-3333-333333333333\"", "\"type\": \"url\", \"url\": \"https://evil.example\"")]
    public void A_changed_address_moves_the_hash(string from, string to)
    {
        var before = Hash(Menu);

        Hash(Edit(from, to)).Should().NotBe(before);
        before.Should().NotBe("(absent)");
    }

    [Theory]
    [InlineData("\"programUnits.setup\"", "\"programUnits.mergeQueueSetup\"")]                // a unit's name
    [InlineData("\"bi-signpost-split\"", "\"bi-gear\"")]                                      // a unit's icon
    [InlineData("\"url\": \"/merge-queue/setup\", \"order\": 2", "\"url\": \"/merge-queue/setup\", \"order\": 7")] // a unit's order
    [InlineData("\"programUnits.groups.main\"", "\"programUnits.groups.start\"")]           // a group's name
    [InlineData("\"icon\": \"bi-house\", \"order\": 1,", "\"icon\": \"bi-gear\", \"order\": 5,")] // a group's icon and order
    [InlineData("\"33333333-3333-3333-3333-333333333333\"", "\"77777777-7777-7777-7777-777777777777\"")] // another unit type's target
    [InlineData("[\"The menu.\"]", "[\"The menu.\", \"One more line.\"]")]                    // a comment
    [InlineData("\"type\": \"url\", \"url\": \"/merge-queue/setup\"", "\"type\": \"URL\", \"url\": \"/merge-queue/setup\"")] // the type's case
    public void Neither_the_presentation_nor_another_unit_moves_the_hash(string from, string to)
        => Hash(Edit(from, to)).Should().Be(Hash(Menu));

    [Fact]
    public void A_url_unit_moved_to_another_group_or_reordered_keeps_the_hash()
    {
        const string moved = """
            {
              "programUnitGroups": [
                { "id": "66666666-6666-6666-6666-666666666666", "name": "programUnits.groups.other", "order": 2, "programUnits": [
                  { "id": "55555555-5555-5555-5555-555555555555", "name": "programUnits.docs", "type": "url", "url": "https://example.com/docs", "order": 1 } ] },
                { "id": "11111111-1111-1111-1111-111111111111", "name": "programUnits.groups.main", "order": 1, "programUnits": [
                  { "id": "44444444-4444-4444-4444-444444444444", "name": "programUnits.setup", "type": "url", "url": "/merge-queue/setup", "order": 2 } ] },
                { "id": "88888888-8888-8888-8888-888888888888", "name": "programUnits.groups.added", "order": 3, "programUnits": [] }
              ]
            }
            """;

        Hash(moved).Should().Be(Hash(Menu), "a unit is keyed by its id, not by the group it sits in");
    }

    [Fact]
    public void A_menu_without_a_url_unit_has_no_entry()
    {
        const string typedOnly = """
            { "programUnitGroups": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "programUnits.groups.main", "order": 1,
              "programUnits": [ { "id": "22222222-2222-2222-2222-222222222222", "name": "programUnits.cars", "type": "query",
                "queryId": "33333333-3333-3333-3333-333333333333", "order": 1 } ] } ] }
            """;

        Hash(typedOnly).Should().Be("(absent)", "nothing in a typed menu is an address a browser follows out of the application");
    }
}
