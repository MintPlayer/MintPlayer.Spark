using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// #264, G-Q7: deleting the visibility write gate (<c>if (!def.IsVisible) return false;</c> in
/// <c>EntityMapper.IsWritableBySchema</c>) must not open a write path on any attribute it used to protect.
/// </summary>
/// <remarks>
/// The rule is checked against the applications' real model and security files, not fixtures, because the
/// risk is exactly an application attribute that was writable only "because nobody draws it".
/// <para>
/// <b>What is checked.</b> The 24 attributes that were <c>"isVisible": false</c> on master (<c>963cb35b</c>,
/// listed below) fall in two groups:
/// <list type="bullet">
/// <item>21 must stay unwritable. Each passes when its model file still declares it and it is
/// <c>isReadOnly: true</c>, or it is denied <c>Edit</c> and <c>New</c> on both well-known groups; or when the
/// model no longer declares it at all (deleted, or <c>[IgnoreProperty]</c>): the write gate refuses a name with
/// no model entry. (A 22nd, CodeCoverage <c>SparkUser.Id</c>, left with the type: SparkUser is now
/// MintPlayer.Spark.Authorization's model, which declares no <c>Id</c>; composition M4.)</item>
/// <item>2 are the bugs (Fleet <c>Car.PoliceReportNumber</c>, HR <c>Person.LastName</c>): they must be declared
/// and writable, because being writable is their fix.</item>
/// </list>
/// The forward-looking half: any attribute a model says is drawn nowhere (<c>showedOn: None</c>) is held to
/// the same rule unless it is one of the two allow-listed attributes, so the next "hide it in the model"
/// cannot quietly reintroduce a writable-but-undrawn field.
/// </para>
/// <para>
/// <b>Why it can fail.</b> Flip any listed attribute's <c>isReadOnly</c> to <c>false</c> in its model file and
/// its case goes red; mark one of the two bugs read-only and theirs does. Each case first asserts that the
/// model file exists, so a renamed file or a wrong root cannot turn the check vacuous.
/// </para>
/// </remarks>
public class HiddenAttributesStayProtectedTests
{
    /// <summary>The 21 attributes hidden on master that must not become writable.</summary>
    public static TheoryData<string, string, string> ProtectedOnMaster => new()
    {
        { "CodeCoverage", "Account", "GitHubId" },
        { "CodeCoverage", "Account", "InstallationId" },
        { "CodeCoverage", "Account", "OwnerKey" },
        { "CodeCoverage", "ForgeAccounts", "Title" },
        { "CodeCoverage", "ForgeAccounts", "Provider" },
        { "CodeCoverage", "Home", "Title" },
        { "CodeCoverage", "MyAccountRow", "Provider" },
        { "CodeCoverage", "MyAccountRow", "Type" },
        { "CodeCoverage", "Repository", "GitHubId" },
        { "CodeCoverage", "Repository", "IsPrivate" },
        { "CodeCoverage", "Repository", "OwnerKey" },
        { "CodeCoverage", "Repository", "PreviousFullNames" },
        { "Fleet", "Car", "CreatedBy" },
        { "QnA", "Answer", "CreatedBy" },
        { "QnA", "Answer", "DeletedBy" },
        { "QnA", "Answer", "ModifiedBy" },
        { "QnA", "Question", "CreatedBy" },
        { "QnA", "Question", "DeletedBy" },
        { "QnA", "Question", "ModifiedBy" },
        { "QnA", "QuestionTranslation", "Key" },
        { "QnA", "QuestionTranslationsContribution", "ContributorId" },
    };

    /// <summary>The 2 attributes hidden on master whose fix is to be writable (PRD §2.3).</summary>
    public static TheoryData<string, string, string> WritableByDesign => new()
    {
        { "Fleet", "Car", "PoliceReportNumber" },
        { "HR", "Person", "LastName" },
    };

    private static readonly HashSet<(string App, string Type, string Attribute)> AllowListed =
    [
        ("Fleet", "Car", "PoliceReportNumber"),
        ("HR", "Person", "LastName"),
    ];

    [Theory]
    [MemberData(nameof(ProtectedOnMaster))]
    public void An_attribute_hidden_on_master_is_still_not_writable(string app, string type, string attribute)
    {
        var declared = Attribute(app, type, attribute);
        if (declared is null)
            return; // Deleted or [IgnoreProperty]: IsWritableBySchema refuses a name with no model entry.

        IsProtected(app, type, attribute, declared).Should().BeTrue(
            $"{app} {type}.{attribute} was protected by isVisible: false alone in the write gate's eyes; it must now be "
            + "isReadOnly: true, denied Edit and New on both well-known groups, or no longer declared");
    }

    [Theory]
    [MemberData(nameof(WritableByDesign))]
    public void A_bug_attribute_is_declared_and_writable(string app, string type, string attribute)
    {
        var declared = Attribute(app, type, attribute);
        declared.Should().NotBeNull($"{app} {type}.{attribute} is user input and must stay in the model");
        IsProtected(app, type, attribute, declared!).Should().BeFalse(
            $"{app} {type}.{attribute} was silently unwritable because it was hidden; being writable is the fix");
    }

    [Fact]
    public void An_attribute_drawn_nowhere_is_not_writable_unless_allow_listed()
    {
        var appsDirectory = Path.Combine(RepositoryRoot(), "apps");
        var modelDirectories = Directory.GetDirectories(appsDirectory)
            .Select(app => (App: Path.GetFileName(app), Model: Path.Combine(app, Path.GetFileName(app), "App_Data", "Model")))
            .Where(x => Directory.Exists(x.Model))
            .ToList();
        // Six applications, five with a model of their own: SparkId's entities all come from the identity
        // provider's library layer, so it has no App_Data/Model in git (an empty folder only exists locally).
        modelDirectories.Count.Should().BeGreaterThanOrEqualTo(5, "the scan must find every application's model directory");

        var offenders = new List<string>();
        foreach (var (app, modelDirectory) in modelDirectories)
        {
            foreach (var file in Directory.GetFiles(modelDirectory, "*.json"))
            {
                var po = JsonNode.Parse(File.ReadAllText(file))?["persistentObject"];
                var type = po?["name"]?.GetValue<string>();
                if (type is null || po?["attributes"] is not JsonArray attributes) continue;

                foreach (var attribute in attributes.OfType<JsonObject>())
                {
                    var name = attribute["name"]?.GetValue<string>();
                    if (name is null || !IsDrawnNowhere(attribute["showedOn"])) continue;
                    if (AllowListed.Contains((app, type, name))) continue;
                    if (!IsProtected(app, type, name, attribute))
                        offenders.Add($"{app} {type}.{name}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "an attribute with showedOn: None is not drawn, so nobody sees what is written to it: protect it with "
            + "isReadOnly or an Edit/New deny, or allow-list it here with the reason it is user input");
    }

    // ---------------------------------------------------------------------------------

    private static bool IsDrawnNowhere(JsonNode? showedOn) => showedOn switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text.Trim() == "None",
        JsonValue value when value.TryGetValue<int>(out var number) => number == 0,
        _ => false,
    };

    private static bool IsProtected(string app, string type, string attribute, JsonNode declared)
        => declared["isReadOnly"]?.GetValue<bool>() == true
           || (DeniedForEveryone(app, type, attribute, "Edit") && DeniedForEveryone(app, type, attribute, "New"));

    /// <summary>
    /// A deny of <paramref name="verb"/> on the attribute (or the whole type) for both well-known groups — the
    /// "everyone" deny of PRD §2.4. An ordinary grant cannot outrank it.
    /// </summary>
    private static bool DeniedForEveryone(string app, string type, string attribute, string verb)
    {
        var path = Path.Combine(AppData(app), "security.json");
        if (!File.Exists(path)) return false;

        var security = JsonNode.Parse(File.ReadAllText(path))!;
        var wellKnown = security["wellKnown"] as JsonObject;
        var everyone = new[] { wellKnown?["anonymous"]?.GetValue<string>(), wellKnown?["authenticated"]?.GetValue<string>() };
        if (everyone.Any(g => g is null)) return false;

        var deniedGroups = (security["rights"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(r => r["isDenied"]?.GetValue<bool>() == true)
            .Where(r =>
            {
                var segments = (r["resource"]?.GetValue<string>() ?? "").Split('/');
                return segments[0].Contains(verb, StringComparison.Ordinal)
                       && segments.Length >= 2 && segments[1] == type
                       && (segments.Length == 2 || (segments.Length == 3 && segments[2] == attribute));
            })
            .Select(r => r["groupId"]?.GetValue<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return everyone.All(g => deniedGroups.Contains(g));
    }

    private static JsonNode? Attribute(string app, string type, string attribute)
    {
        var file = Path.Combine(AppData(app), "Model", $"{type}.json");
        File.Exists(file).Should().BeTrue($"the model file of {app} {type} must exist at {file}");

        var po = JsonNode.Parse(File.ReadAllText(file))!["persistentObject"]!;
        return (po["attributes"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .FirstOrDefault(a => a["name"]?.GetValue<string>() == attribute);
    }

    private static string AppData(string app) => Path.Combine(RepositoryRoot(), "apps", app, app, "App_Data");

    /// <summary>Walks up from the test binary to the directory holding the solution.</summary>
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
