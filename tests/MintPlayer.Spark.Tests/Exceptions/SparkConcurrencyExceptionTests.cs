using MintPlayer.Spark.Exceptions;

namespace MintPlayer.Spark.Tests.Exceptions;

public class SparkConcurrencyExceptionTests
{
    [Fact]
    public void Stores_both_etag_values()
    {
        var ex = new SparkConcurrencyException(expectedEtag: "abc", actualEtag: "xyz");

        ex.ExpectedEtag.Should().Be("abc");
        ex.ActualEtag.Should().Be("xyz");
    }

    [Fact]
    public void Message_mentions_both_etag_values_when_actual_is_present()
    {
        var ex = new SparkConcurrencyException(expectedEtag: "abc", actualEtag: "xyz");

        ex.Message.Should().Contain("abc").And.Contain("xyz");
    }

    [Fact]
    public void Message_uses_placeholder_when_actual_etag_is_null()
    {
        var ex = new SparkConcurrencyException(expectedEtag: "abc", actualEtag: null);

        ex.ActualEtag.Should().BeNull();
        ex.Message.Should().Contain("abc").And.Contain("<none>");
    }
}
