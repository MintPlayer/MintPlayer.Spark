using System.Text.Json;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>
/// <see cref="JsonFixtureImporter"/> stores what the fixture says, byte for byte where dates are
/// concerned (PRD F1, <c>docs/datetimeoffset_query_sort_filter_PRD.md</c>).
/// </summary>
/// <remarks>
/// Measured before the fix: <c>"2027-03-01T09:00:00.0000000-05:00"</c> was stored as
/// <c>"2027-03-01T15:00:00.0000000"</c> on a machine in Brussels — the instant moved to the local
/// clock and the offset was gone, so the stored value depended on the machine's zone and on DST.
/// Only a <c>Z</c> value survived. The assertion is on the raw stored JSON rather than on a typed
/// load, because a typed load re-reads the damage as a plausible-looking local value.
/// </remarks>
public class JsonFixtureImporterTests : SparkTestDriver
{
    [Theory]
    [InlineData("2027-03-01T09:00:00.0000000-05:00")]
    [InlineData("2027-03-02T01:00:00.0000000+12:00")]
    [InlineData("2027-03-01T22:15:00.0000000+05:45")]
    [InlineData("2027-03-01T15:00:00.0000000+00:00")]
    [InlineData("2027-03-01T15:00:00.0000000Z")]
    [InlineData("2027-06-01T09:00:00.0000000-05:00")]
    public async Task A_date_string_is_stored_exactly_as_written(string written)
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"spark-fixture-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(fixture, $$"""
            { "Results": [ { "At": "{{written}}", "@metadata": { "@id": "stamps/1", "@collection": "Stamps" } } ] }
            """);

        try
        {
            await SeedFromJsonAsync(fixture);
        }
        finally
        {
            File.Delete(fixture);
        }

        (await StoredStringAsync("stamps/1", "At")).Should().Be(written);
    }

    /// <summary>The field as the server holds it, read over HTTP so no client conversion can intervene.</summary>
    private async Task<string?> StoredStringAsync(string id, string field)
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync(
            $"{Store.Urls[0]}/databases/{Uri.EscapeDataString(Store.Database)}/docs?id={Uri.EscapeDataString(id)}");

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Results")[0].GetProperty(field).GetString();
    }
}
