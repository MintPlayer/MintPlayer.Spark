using MintPlayer.Spark.Prerendering;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// The .NET request key must equal what <c>sparkServerRequestKey</c> (ng-spark/ssr) computes in the render,
/// byte for byte, or the page misses the supplied response (docs/spark_ssr_PRD.md I1). Each expected
/// string was produced by running the TypeScript algorithm in Node on the equivalent JavaScript object.
/// </summary>
public class SparkPrerenderRequestKeyTests
{
    [Fact]
    public void A_null_member_is_dropped_as_JSON_stringify_drops_undefined()
    {
        var body = SparkPrerenderRequestKey.CanonicalBody(new { objectTypeId = "song", id = "Songs/23", deleted = (string?)null });

        body.Should().Be("{\"id\":\"Songs/23\",\"objectTypeId\":\"song\"}");
    }

    [Fact]
    public void Keys_sort_at_every_depth_and_strings_escape_as_JavaScript_escapes_them()
    {
        var body = SparkPrerenderRequestKey.CanonicalBody(new
        {
            queryId = "q",
            sortColumns = new[] { new { property = "Name", direction = "asc" } },
            search = "é\"\n\u0001😀<>&",
            take = 50,
            skip = 0,
        });

        body.Should().Be("{\"queryId\":\"q\",\"search\":\"é\\\"\\n\\u0001😀<>&\",\"skip\":0,\"sortColumns\":[{\"direction\":\"asc\",\"property\":\"Name\"}],\"take\":50}");
    }

    [Fact]
    public void Keys_sort_ordinally_by_UTF16_code_unit()
    {
        var body = SparkPrerenderRequestKey.CanonicalBody(new Dictionary<string, object>
        {
            ["b"] = 1,
            ["B"] = 2,
            ["_"] = 3,
            ["a"] = new Dictionary<string, object> { ["z"] = true, ["y"] = false },
        });

        body.Should().Be("{\"B\":2,\"_\":3,\"a\":{\"y\":false,\"z\":true},\"b\":1}");
    }

    [Fact]
    public void No_body_is_the_empty_string()
    {
        SparkPrerenderRequestKey.For("get", "/spark/culture", SparkPrerenderRequestKey.CanonicalBody(null))
            .Should().Be("GET /spark/culture ");
    }
}
