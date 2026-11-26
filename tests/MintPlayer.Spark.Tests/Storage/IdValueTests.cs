using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// <see cref="Id{T}"/> as a value (#536 D23): ordinal ordering, also through the untyped <see cref="IComparable"/> that
/// sorts boxed values, and its System.Text.Json form, a string read lower case (D7) with <c>null</c> for the empty id.
/// </summary>
public class IdValueTests
{
    private sealed class Book;

    private static readonly Id<Book> One = Id<Book>.FromTrusted("books/1");
    private static readonly Id<Book> AlsoOne = Id<Book>.FromTrusted("BOOKS/1");
    private static readonly Id<Book> Two = Id<Book>.FromTrusted("books/2");
    private static readonly Id<Book> Ten = Id<Book>.FromTrusted("books/10");

    [Fact]
    public void The_operators_order_the_canonical_strings_ordinally()
    {
        (One < Two).Should().BeTrue();
        (Two < One).Should().BeFalse();
        (Two > One).Should().BeTrue();
        (One > Two).Should().BeFalse();
        (One <= AlsoOne).Should().BeTrue();
        (Two <= One).Should().BeFalse();
        (One >= AlsoOne).Should().BeTrue();
        (One >= Two).Should().BeFalse();

        // Text order, not numeric: the class summary's "numeric ids sort as text on RavenDB".
        (Ten < Two).Should().BeTrue();
    }

    [Fact]
    public void Boxed_ids_sort_and_the_untyped_CompareTo_refuses_anything_but_an_id()
    {
        object[] boxed = [Two, One];
        Array.Sort(boxed);
        boxed.Should().Equal([One, Two]);

        IComparable comparable = One;
        comparable.CompareTo(null).Should().BeGreaterThan(0);
        var act = () => comparable.CompareTo("books/1");
        act.Should().Throw<ArgumentException>().WithParameterName("obj");
    }

    [Fact]
    public void Json_reads_a_string_lower_case_and_null_as_the_empty_id()
    {
        JsonSerializer.Deserialize<Id<Book>>("\"Books/ABC\"").Value.Should().Be("books/abc");
        JsonSerializer.Deserialize<Id<Book>>("null").IsEmpty.Should().BeTrue();

        var act = () => JsonSerializer.Deserialize<Id<Book>>("42");
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Json_writes_the_canonical_string_and_null_for_the_empty_id()
    {
        JsonSerializer.Serialize(AlsoOne).Should().Be("\"books/1\"");
        JsonSerializer.Serialize(default(Id<Book>)).Should().Be("null");
    }
}
