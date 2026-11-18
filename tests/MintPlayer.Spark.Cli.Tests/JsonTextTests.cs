using MintPlayer.Spark.Cli;

namespace MintPlayer.Spark.Cli.Tests;

/// <summary>
/// <see cref="JsonText"/> edits App_Data files as text, so comments, key order and the author's layout survive (the
/// template engine's JSON post-action rewrites the whole file and drops comments: plan M3 spike).
/// </summary>
public class JsonTextTests
{
    private const string Security = """
        {
          "$schema": "https://schemas.spark.mintplayer.com/v14/security.schema.json",
          // Who may do what.
          "groups": {
            "00000000-0000-4000-8000-000000000010": "Administrators"
          },

          "rights": [
            { "key": "people", "resource": "QueryReadEditNewDelete/Person", "groupId": "@anonymous" }
          ]
        }
        """;

    [Fact]
    public void AppendToArray_adds_the_item_after_the_last_one_with_its_indentation()
    {
        var result = JsonText.AppendToArray(Security, "rights", """{ "key": "invoice", "resource": "QueryRead/Invoice", "groupId": "x" }""");

        result.Should().Be("""
            {
              "$schema": "https://schemas.spark.mintplayer.com/v14/security.schema.json",
              // Who may do what.
              "groups": {
                "00000000-0000-4000-8000-000000000010": "Administrators"
              },

              "rights": [
                { "key": "people", "resource": "QueryReadEditNewDelete/Person", "groupId": "@anonymous" },
                { "key": "invoice", "resource": "QueryRead/Invoice", "groupId": "x" }
              ]
            }
            """);
    }

    [Fact]
    public void AppendToArray_fills_an_empty_array()
    {
        var result = JsonText.AppendToArray("{\n  \"rights\": []\n}", "rights", "1");

        result.Should().Be("{\n  \"rights\": [\n    1\n  ]\n}");
    }

    [Fact]
    public void AppendToArray_ignores_brackets_and_property_names_inside_strings_and_comments()
    {
        const string json = """
            {
              "_comment": "the \"rights\": [ array ]",
              /* "rights": [ ] */
              "rights": [ 1 ] // trailing ]
            }
            """;

        JsonText.AppendToArray(json, "rights", "2").Should().Be("""
            {
              "_comment": "the \"rights\": [ array ]",
              /* "rights": [ ] */
              "rights": [ 1, 2 ] // trailing ]
            }
            """);
    }

    [Fact]
    public void AppendToArray_refuses_a_file_without_the_property()
    {
        var act = () => JsonText.AppendToArray("{ \"groups\": {} }", "rights", "1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*\"rights\"*");
    }

    [Fact]
    public void AddProperty_adds_a_member_at_the_end_of_the_root_object()
    {
        const string json = """
            {
              "$schema": "../schemas/actions.schema.json",
              "Copy": { "icon": "Copy" }
            }
            """;

        JsonText.AddProperty(json, "Approve", """{ "showedOn": "both" }""").Should().Be("""
            {
              "$schema": "../schemas/actions.schema.json",
              "Copy": { "icon": "Copy" },
              "Approve": { "showedOn": "both" }
            }
            """);
    }

    [Fact]
    public void AddProperty_fills_an_empty_object()
    {
        JsonText.AddProperty("{}", "Approve", "true").Should().Be("{\n  \"Approve\": true\n}");
    }

    [Fact]
    public void HasProperty_finds_root_members_only()
    {
        const string json = """{ "Copy": { "Approve": 1 }, "_comment": "\"Approve\"" }""";

        JsonText.HasProperty(json, "Copy").Should().BeTrue();
        JsonText.HasProperty(json, "Approve").Should().BeFalse();
    }

    [Fact]
    public void ArrayContains_matches_a_string_value_of_any_item()
    {
        JsonText.ArrayContainsValue(Security, "rights", "key", "people").Should().BeTrue();
        JsonText.ArrayContainsValue(Security, "rights", "key", "invoice").Should().BeFalse();
    }
}
