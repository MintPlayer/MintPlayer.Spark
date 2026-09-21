using System.Text.Json;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// Connection state and forge identity are maintained by the seam, never by a person.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <c>Connection</c>, <c>DisconnectedReason</c> and <c>DisconnectedAtUtc</c> describe what the
/// forge told us — a webhook, or the reconcile sweep. Nothing a user types can make an App
/// installed again, so an editable field here is a lie in the interface: the save succeeds, the page
/// shows what was typed, and the next reconcile silently overwrites it. Worse, a hand-set
/// <c>Connected</c> re-enables listing and upload paths that gate on it, until the sweep undoes it.
/// </para>
/// <para>
/// <b>Found on production.</b> <c>Repository</c> and <c>GitHubProject</c> had all three read-only
/// from the start; <c>Account</c> gained them later, from the M8 connection-state work, and was
/// missed — so the account edit page offered three editable fields that nothing honoured. This test
/// exists because the inconsistency was invisible: each model is valid on its own, and only a
/// comparison across them shows one is wrong.
/// </para>
/// <para>
/// Written over every model that declares these attributes rather than against a named list, so a
/// fourth <c>IForgeConnectable</c> is covered the day its model is generated.
/// </para>
/// </remarks>
public class ConnectionStateIsReadOnlyTests
{
    private static readonly string[] MachineMaintained =
        ["Connection", "DisconnectedReason", "DisconnectedAtUtc",
         // ⚠️ Identity, and just as un-editable. `Provider` is part of the document id
         // (`Accounts/github/48772716`), so an edit would desynchronise the field from the key it is
         // filed under; `OwnerKey` is a get-only computed property, so the model was advertising an
         // editable field for something with no setter at all.
         "Provider", "OwnerKey"];

    private static string ModelDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MintPlayer.Spark.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the test must run somewhere under the repository root");
        return Path.Combine(dir!, "apps", "CodeCoverage", "CodeCoverage", "App_Data", "Model");
    }

    /// <summary>Every model carrying at least one of the connection attributes.</summary>
    public static TheoryData<string> ModelsWithConnectionState()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(ModelDirectory(), "*.json"))
        {
            if (Path.GetFileName(path).Equals("modelHashes.json", StringComparison.OrdinalIgnoreCase))
                continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("persistentObject", out var po))
                continue;
            if (!po.TryGetProperty("attributes", out var attributes))
                continue;

            var carries = attributes.EnumerateArray()
                .Any(a => MachineMaintained.Contains(a.GetProperty("name").GetString()));

            if (carries)
                data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    /// <summary>
    /// ⚠️ This must find more than one model, or it would pass by finding nothing — the failure
    /// mode of every "for each file" test.
    /// </summary>
    [Fact]
    public void The_sweep_finds_the_models_it_is_meant_to_cover()
    {
        ModelsWithConnectionState().Count.Should().BeGreaterThanOrEqualTo(
            3, "Account, Repository and GitHubProject all implement IForgeConnectable");
    }

    /// <summary>
    /// ⚠️ On the account page, <b>exactly one</b> attribute is editable.
    /// </summary>
    /// <remarks>
    /// The owner's rule, stated directly: <i>"only Delete branch on merge should be editable"</i>.
    /// Everything else on an account is either forge identity, which is part of the document id, or
    /// connection state, which the reconcile sweep owns — so an editable field there accepts input
    /// that nothing honours.
    /// <para>
    /// Asserted as an exact set rather than as "these named ones are read-only", because the failure
    /// this guards against is an attribute being <em>added</em> later without the flag. A per-name
    /// list cannot see a name it does not know.
    /// </para>
    /// </remarks>
    [Fact]
    public void Only_the_branch_deletion_default_is_editable_on_an_account()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDirectory(), "Account.json")));

        var editable = doc.RootElement.GetProperty("persistentObject").GetProperty("attributes")
            .EnumerateArray()
            .Where(a => !a.GetProperty("isReadOnly").GetBoolean())
            .Select(a => a.GetProperty("name").GetString()!)
            .ToArray();

        editable.Should().Equal(
            ["DeleteBranchOnPrClose"],
            "an account carries nothing else a person may set — identity is part of its document id "
            + "and connection state belongs to the reconcile sweep");
    }

    [Theory]
    [MemberData(nameof(ModelsWithConnectionState))]
    public void Connection_state_is_not_editable(string entity)
    {
        var path = Path.Combine(ModelDirectory(), $"{entity}.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        var editable = doc.RootElement.GetProperty("persistentObject").GetProperty("attributes")
            .EnumerateArray()
            .Where(a => MachineMaintained.Contains(a.GetProperty("name").GetString())
                && !a.GetProperty("isReadOnly").GetBoolean())
            .Select(a => a.GetProperty("name").GetString()!)
            .ToArray();

        editable.Should().BeEmpty(
            "connection state on {0} is written by webhooks and the reconcile sweep, so an editable "
            + "field accepts a value that the next reconcile silently overwrites — set isReadOnly "
            + "on it in {0}.json",
            entity);
    }
}
