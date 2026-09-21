using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// The forge-webhook reference documents every event that exists.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/code-coverage/forge-webhooks.md</c> is what somebody porting GitLab or Bitbucket reads to
/// find out what they are expected to raise. A seventh event added to the vocabulary and not to that
/// table would be invisible to them — they would implement six, ship, and discover the omission from
/// whatever quietly stopped working.
/// </para>
/// <para>
/// ⚠️ This asserts <b>presence, not correctness</b>. It cannot tell whether the GitLab column names
/// the right hook, and it does not try — that column is explicitly marked unverified in the document
/// itself. What it can guarantee is that no event is missing from the page altogether, which is the
/// failure mode a reader cannot detect for themselves.
/// </para>
/// </remarks>
public class ForgeWebhookDocsTests
{
    private static string ReferenceDoc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "docs", "code-coverage", "forge-webhooks.md");
        Assert.True(File.Exists(path), $"Expected the forge webhook reference at {path}");
        return File.ReadAllText(path);
    }

    private static IEnumerable<Type> NeutralEvents()
        => typeof(ForgeEvents).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IForgeEvent).IsAssignableFrom(t));

    [Fact]
    public void Every_neutral_event_appears_in_the_reference()
    {
        var doc = ReferenceDoc();
        var missing = NeutralEvents()
            .Select(t => t.Name)
            .Where(name => !doc.Contains(name, StringComparison.Ordinal))
            .OrderBy(n => n)
            .ToArray();

        Assert.True(missing.Length == 0,
            "These events exist but the forge-webhook reference never mentions them, so a second "
            + "forge would not know to raise them:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The mapping table has a column per forge the vocabulary claims to serve.
    /// </summary>
    [Fact]
    public void The_reference_maps_every_forge_the_vocabulary_claims_to_serve()
    {
        var doc = ReferenceDoc();

        foreach (var provider in Enum.GetNames<EForgeProvider>())
            Assert.Contains(provider, doc, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Proves the finder reads a real document, so a green run cannot mean "found nothing".
    /// </summary>
    [Fact]
    public void The_reference_is_actually_read()
    {
        var doc = ReferenceDoc();

        Assert.True(doc.Length > 2000, "The reference looks truncated.");
        Assert.Contains("IRecipient<ForgeWebhookMessage<", doc, StringComparison.Ordinal);
    }
}
