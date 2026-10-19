namespace MintPlayer.Spark.Gs1.Tests;

/// <summary>
/// Check digits (GTIN, SSCC, location), GTIN normalisation and SSCC allocation (docs/warehouse_picking_plan.md WM2).
/// GTIN and SSCC values are GS1's published examples (GS1 General Specifications §7.9; the SSCC is the one in this
/// package's README); location digits come from the FNV-1a reference in <see cref="LocationCheckDigits"/>.
/// </summary>
public class Gs1KeyTests
{
    [Theory]
    [InlineData("400638133393", 1)]        // GTIN-13 4006381333931
    [InlineData("9638507", 4)]             // GTIN-8 96385074
    [InlineData("03600029145", 2)]         // UPC-A 036000291452
    [InlineData("09521234000000001", 3)]   // SSCC 095212340000000013
    public void Check_digit_matches_GS1(string data, int expected)
        => Gs1CheckDigit.Compute(data).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("12a4")]
    public void Check_digit_rejects_non_digits(string data)
        => ((Action)(() => Gs1CheckDigit.Compute(data))).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("4006381333931", "04006381333931")]
    [InlineData("96385074", "00000096385074")]
    [InlineData("036000291452", "00036000291452")]
    [InlineData("04006381333931", "04006381333931")]
    public void Gtin_normalises_to_14_digits(string gtin, string expected)
        => Gs1Gtin.Normalize(gtin).Should().Be(expected);

    [Theory]
    [InlineData("4006381333932")]   // wrong check digit
    [InlineData("400638133393")]    // 12 digits, but not a valid UPC-A
    [InlineData("1234567890")]      // no GTIN length
    [InlineData("400638133393X")]
    [InlineData(null)]
    public void Gtin_rejects_invalid_codes(string? gtin)
    {
        Gs1Gtin.IsValid(gtin).Should().BeFalse();
        Gs1Gtin.Normalize(gtin).Should().BeNull();
    }

    [Theory]
    [InlineData("A-01-02-3", "", 2, "08")]
    [InlineData("A-01-02-3", "site-secret", 2, "68")]
    [InlineData("A-01-02-3", "", 4, "8808")]
    [InlineData("B-12-01-1", "", 2, "74")]
    public void Location_check_digits_are_the_reference_values(string code, string secret, int length, string expected)
    {
        LocationCheckDigits.Compute(code, secret, length).Should().Be(expected);
        LocationCheckDigits.IsValid(code, expected, secret, length).Should().BeTrue();
    }

    [Fact]
    public void Location_check_digits_reject_a_wrong_or_short_entry()
    {
        LocationCheckDigits.IsValid("A-01-02-3", "09").Should().BeFalse();
        LocationCheckDigits.IsValid("A-01-02-3", "8").Should().BeFalse();
        LocationCheckDigits.IsValid("A-01-02-3", null).Should().BeFalse();
    }

    [Fact]
    public async Task Sscc_allocator_hands_out_consecutive_serials_with_check_digits()
    {
        var allocator = new SsccAllocator(new InMemorySsccSerialSource(firstSerial: 1), 0, "9521234");

        var first = await allocator.AllocateAsync();
        var second = await allocator.AllocateAsync();

        first.Should().Be("095212340000000013");
        second.Should().Be("09521234000000002" + Gs1CheckDigit.Compute("09521234000000002"));
        Gs1CheckDigit.IsValid(second).Should().BeTrue();
        allocator.SequenceKey.Should().Be("gs1/sscc/09521234");
        allocator.MaxSerial.Should().Be(999_999_999);
    }

    [Fact]
    public async Task Sscc_allocator_never_repeats_under_concurrency()
    {
        var allocator = new SsccAllocator(new InMemorySsccSerialSource(), 3, "5412345678");

        var ssccs = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => allocator.AllocateAsync().AsTask()));

        ssccs.Distinct().Count().Should().Be(200);
        ssccs.All(s => s.Length == 18 && s[0] == '3' && Gs1CheckDigit.IsValid(s)).Should().BeTrue();
    }

    [Fact]
    public async Task Sscc_allocator_refuses_an_exhausted_range()
    {
        // A 10-digit prefix leaves 6 serial digits: 999999 is the last serial.
        var allocator = new SsccAllocator(new InMemorySsccSerialSource(firstSerial: 999_999), 0, "5412345678");

        (await allocator.AllocateAsync()).Should().Be("05412345678999999" + Gs1CheckDigit.Compute("05412345678999999"));
        await ((Func<Task>)(async () => await allocator.AllocateAsync())).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(10, "9521234")]
    [InlineData(-1, "9521234")]
    [InlineData(0, "952123")]
    [InlineData(0, "95212345678")]
    [InlineData(0, "95212A4")]
    public void Sscc_allocator_rejects_an_invalid_prefix_or_extension_digit(int extensionDigit, string prefix)
        => ((Action)(() => new SsccAllocator(new InMemorySsccSerialSource(), extensionDigit, prefix)))
            .Should().Throw<ArgumentException>();
}
