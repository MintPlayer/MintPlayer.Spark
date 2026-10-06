using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK011 against a right whose action is a custom action declared in <c>actions.json</c>.
/// The analyzer reads that file's root keys, and it must read them however the file is indented:
/// it used to match <c>^\s{2}"</c>, so a tab- or four-space-indented file made every custom right
/// read as a typo.
/// </summary>
public class SecurityConfigurationAnalyzerCustomActionsTests
{
    private const string ModelJson = """
        {
          "persistentObject": {
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Person",
            "clrType": "App.Entities.Person",
            "attributes": []
          }
        }
        """;

    private static string SecurityJson(string resource) => $$"""
        {
          "groups": {
            "00000000-0000-0000-0000-000000000001": "Signed-in users"
          },
          "rights": [
            { "key": "22222222-2222-2222-2222-222222222222", "resource": "{{resource}}", "groupId": "00000000-0000-0000-0000-000000000001" }
          ]
        }
        """;

    private static string CustomActions(string indent) =>
        "{\n"
        + $"{indent}\"CarCopy\": {{\n"
        + $"{indent}{indent}\"icon\": \"Copy \\\"car\\\" {{now}}\",\n"
        + $"{indent}{indent}\"tags\": [\"a\", \"b\"]\n"
        + $"{indent}}},\n"
        + $"{indent}\"Archive\": {{}}\n"
        + "}\n";

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string resource, string customActions)
        => GeneratorHarness.RunAnalyzerAsync(
            "SecurityConfigurationAnalyzer",
            ["class Placeholder { }"],
            additionalTexts:
            [
                ("C:\\app\\App_Data\\security.json", SecurityJson(resource)),
                ("C:\\app\\App_Data\\actions.json", customActions),
                ("C:\\app\\App_Data\\Model\\Person.json", ModelJson),
            ]);

    public static TheoryData<string> Layouts() => new()
    {
        CustomActions("  "),
        CustomActions("    "),
        CustomActions("\t"),
        CustomActions("").Replace("\n", string.Empty),
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task A_declared_custom_action_is_known_whatever_the_indentation(string customActions)
    {
        (await RunAsync("CarCopy/Person", customActions)).Should().BeEmpty();
        (await RunAsync("Archive/Person", customActions)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task A_nested_key_is_not_mistaken_for_a_custom_action(string customActions)
    {
        // Depth, not indentation, decides what is a root key — so `icon` stays a property of
        // CarCopy even in a minified file where every key sits on the same line.
        var diagnostics = await RunAsync("icon/Person", customActions);

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK011");
    }
}
