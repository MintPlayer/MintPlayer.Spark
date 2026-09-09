using System.Text.Json;
using Xunit;

namespace CodeCoverage.Tests.ApiTokens;

/// <summary>
/// The token hash must never reach a client.
/// </summary>
/// <remarks>
/// <c>ApiToken</c> is a Spark persistent object, so Spark generates a query grid and a per-object
/// endpoint over a collection of credentials. Spark projects exactly the attributes the model
/// declares, so what keeps the hash off the wire is that <c>Hash</c> carries
/// <c>[IgnoreProperty]</c> and therefore has no attribute block.
/// <para>
/// ⚠️ Omitting it by hand would not survive: model synchronization <em>discovers</em> public
/// properties and would put it straight back. This test fails if someone drops the attribute, and
/// it reads the committed model file rather than the CLR type, because the model file is what the
/// server actually serves from.
/// </para>
/// <para>
/// The document id is a guid for the same reason — a PersistentObject always carries its id, so the
/// old <c>ApiTokens/{sha256}</c> scheme would have shipped the hash regardless of the model.
/// </para>
/// </remarks>
public class ApiTokenHashIsNeverProjectedTests
{
    private static JsonElement Model()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "App_Data", "Model", "ApiToken.json");
        if (!File.Exists(path))
        {
            // Fall back to the source tree when the model is not copied to the output.
            path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "CodeCoverage", "App_Data", "Model", "ApiToken.json"));
        }

        Assert.True(File.Exists(path), $"ApiToken.json not found (looked at {path})");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void The_model_declares_no_Hash_attribute()
    {
        var names = Model()
            .GetProperty("persistentObject")
            .GetProperty("attributes")
            .EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToArray();

        names.Should().NotContain("Hash",
            "an attribute in the model is projected onto every PersistentObject the caller can read");
    }

    /// <summary>
    /// The breadcrumb is rendered server-side and shipped as a string, so a template naming the hash
    /// would leak it past the attribute check. Synchronization picked <c>{Hash}</c> on its own the
    /// first time, which is exactly why this is pinned.
    /// </summary>
    [Fact]
    public void The_breadcrumb_template_does_not_name_the_hash()
    {
        var breadcrumb = Model().GetProperty("persistentObject").GetProperty("breadcrumb").GetString();

        breadcrumb.Should().NotContain("Hash");
        breadcrumb.Should().Be("{Description}");
    }
}
