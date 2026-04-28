using System.Drawing;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Tests.Converters;

/// <summary>
/// Round-trip tests for the internal <c>ColorNewtonsoftJsonConverter</c> — RavenDB stores
/// <see cref="Color"/> values via Newtonsoft and reads them back through this converter.
/// A regression in the hex format breaks colour persistence on every entity that has a
/// <c>Color</c> attribute.
/// </summary>
public class ColorNewtonsoftJsonConverterTests
{
    // The converter is internal; we hit it indirectly through the standard Newtonsoft
    // pipeline, which is how Raven invokes it in production.
    private static readonly JsonSerializerSettings Settings;

    static ColorNewtonsoftJsonConverterTests()
    {
        Settings = new JsonSerializerSettings();
        var converterType = typeof(MintPlayer.Spark.Services.IEntityMapper).Assembly
            .GetType("MintPlayer.Spark.Converters.ColorNewtonsoftJsonConverter")
            ?? throw new InvalidOperationException("ColorNewtonsoftJsonConverter type not found.");
        var converter = (Newtonsoft.Json.JsonConverter)Activator.CreateInstance(converterType)!;
        Settings.Converters.Add(converter);
    }

    private sealed class Holder
    {
        public Color Tint { get; set; }
    }

    [Fact]
    public void Writes_color_as_six_digit_lowercase_hex_with_leading_hash()
    {
        var json = JsonConvert.SerializeObject(
            new Holder { Tint = Color.FromArgb(0x12, 0x34, 0x56) }, Settings);

        json.Should().Be("{\"Tint\":\"#123456\"}");
    }

    [Fact]
    public void Writes_empty_color_as_json_null()
    {
        var json = JsonConvert.SerializeObject(
            new Holder { Tint = Color.Empty }, Settings);

        json.Should().Be("{\"Tint\":null}");
    }

    [Fact]
    public void Reads_lowercase_hex_back_to_argb_components()
    {
        var holder = JsonConvert.DeserializeObject<Holder>(
            "{\"Tint\":\"#abcdef\"}", Settings)!;

        holder.Tint.R.Should().Be(0xAB);
        holder.Tint.G.Should().Be(0xCD);
        holder.Tint.B.Should().Be(0xEF);
    }

    [Fact]
    public void Reads_uppercase_hex_back_to_argb_components()
    {
        var holder = JsonConvert.DeserializeObject<Holder>(
            "{\"Tint\":\"#ABCDEF\"}", Settings)!;

        holder.Tint.R.Should().Be(0xAB);
        holder.Tint.G.Should().Be(0xCD);
        holder.Tint.B.Should().Be(0xEF);
    }

    [Fact]
    public void Reads_json_null_back_as_Color_Empty()
    {
        var holder = JsonConvert.DeserializeObject<Holder>(
            "{\"Tint\":null}", Settings)!;

        holder.Tint.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Round_trip_preserves_color_components()
    {
        var original = Color.FromArgb(255, 0, 128);
        var json = JsonConvert.SerializeObject(new Holder { Tint = original }, Settings);
        var roundTripped = JsonConvert.DeserializeObject<Holder>(json, Settings)!.Tint;

        roundTripped.R.Should().Be(original.R);
        roundTripped.G.Should().Be(original.G);
        roundTripped.B.Should().Be(original.B);
    }
}
