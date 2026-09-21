using System.Text.Json;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// A persistent object that declares tabs must put <b>every visible attribute</b> in a group.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>An attribute with no group does not go into the declared tab — it creates a second one.</b>
/// Spark renders ungrouped attributes in an implicit tab of its own, and that tab carries the same
/// default label as the declared one. On the production account page that showed as <b>two tabs both
/// called "Algemeen"</b>, one holding <c>Provider</c> and <c>OwnerKey</c> and the other holding
/// everything else.
/// </para>
/// <para>
/// It is a quiet failure in both directions: the model is valid, synchronize is happy, the CI hash
/// gate is green, and nothing is missing from the page — it is just in a duplicate tab nobody meant
/// to create. And it comes back every time an attribute is added without a group, which is the
/// normal thing to do, because most entities here declare no tabs at all and do not need one.
/// </para>
/// <para>
/// Three of the five attributes that caused it — <c>Connection</c>, <c>DisconnectedReason</c> and
/// <c>DisconnectedAtUtc</c> — were added to <c>Account</c> by the M8 connection-state work and had
/// not reached production yet, so fixing only what was visible on the page would have fixed it until
/// the next deploy.
/// </para>
/// </remarks>
public class AttributeTabGuardTests
{
    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MintPlayer.Spark.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the test must run somewhere under the repository root");
        return dir!;
    }

    private static string ModelDirectory()
        => Path.Combine(RepositoryRoot(), "apps", "CodeCoverage", "CodeCoverage", "App_Data", "Model");

    /// <summary>
    /// Every model that declares at least one tab, so the rule applies to whatever is added next
    /// rather than to a hard-coded list. Today that is <c>Account</c> alone.
    /// </summary>
    public static TheoryData<string> ModelsWithTabs()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(ModelDirectory(), "*.json"))
        {
            if (Path.GetFileName(path).Equals("modelHashes.json", StringComparison.OrdinalIgnoreCase))
                continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("persistentObject", out var po))
                continue;
            if (!po.TryGetProperty("tabs", out var tabs) || tabs.GetArrayLength() == 0)
                continue;

            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ModelsWithTabs))]
    public void A_model_that_declares_tabs_leaves_no_attribute_ungrouped(string entity)
    {
        var path = Path.Combine(ModelDirectory(), $"{entity}.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var po = doc.RootElement.GetProperty("persistentObject");

        var ungrouped = po.GetProperty("attributes").EnumerateArray()
            .Where(a => !a.TryGetProperty("group", out var g)
                || string.IsNullOrWhiteSpace(g.GetString()))
            .Select(a => a.GetProperty("name").GetString()!)
            .ToArray();

        ungrouped.Should().BeEmpty(
            "{0} declares tabs, so an attribute without a group renders in a SECOND, implicit tab "
            + "with the same label as the declared one — assign it to a group in {0}.json",
            entity);
    }

    /// <summary>
    /// Every group points at a tab that exists, so a mistyped id cannot orphan a whole group the
    /// same way a missing group orphans an attribute.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModelsWithTabs))]
    public void Every_group_belongs_to_a_declared_tab(string entity)
    {
        var path = Path.Combine(ModelDirectory(), $"{entity}.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var po = doc.RootElement.GetProperty("persistentObject");

        var tabIds = po.GetProperty("tabs").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var group in po.GetProperty("groups").EnumerateArray())
        {
            var name = group.GetProperty("name").GetString();
            group.TryGetProperty("tab", out var tab).Should().BeTrue($"group '{name}' must name a tab");
            tabIds.Should().Contain(tab.GetString()!, $"group '{name}' points at a tab that does not exist");
        }
    }

    /// <summary>
    /// ⚠️ <c>OwnerKey</c> stays hidden on the account page: it is <c>provider:login</c> for the
    /// account you are already looking at, so it restates the page's own identity in a field that
    /// looks editable.
    /// </summary>
    [Fact]
    public void The_account_page_does_not_show_its_own_owner_key()
    {
        var path = Path.Combine(ModelDirectory(), "Account.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        var ownerKey = doc.RootElement.GetProperty("persistentObject").GetProperty("attributes")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "OwnerKey");

        ownerKey.GetProperty("isVisible").GetBoolean().Should().BeFalse(
            "the account page is already scoped to this owner, so showing its key adds nothing");
    }
}
