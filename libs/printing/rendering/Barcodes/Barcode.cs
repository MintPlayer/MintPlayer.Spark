using QRCoder;

namespace MintPlayer.Spark.Printing.Rendering.Barcodes;

/// <summary>The symbologies labels use (WX6; PRD §4.11).</summary>
public enum Symbology
{
    Code128,
    /// <summary>Code 128 with FNC1: <see cref="Barcode.Elements"/> hold the element strings.</summary>
    Gs1_128,
    Ean13,
    DataMatrix,
    /// <summary>Data Matrix with FNC1: <see cref="Barcode.Elements"/> hold the element strings.</summary>
    Gs1DataMatrix,
    Qr,
}

/// <summary>
/// A barcode to draw: its symbology and content. A ZPL label prints it with the printer's own command; a PDF draws the
/// modules this package encodes. Build with the factory methods.
/// </summary>
public sealed record Barcode(Symbology Symbology, string Text, IReadOnlyList<KeyValuePair<string, string>>? Elements = null)
{
    public static Barcode Code128(string text) => new(Symbology.Code128, text);

    public static Barcode Ean13(string digits) => new(Symbology.Ean13, Barcodes.Ean13.Complete(digits));

    public static Barcode DataMatrix(string text) => new(Symbology.DataMatrix, text);

    public static Barcode Qr(string text) => new(Symbology.Qr, text);

    /// <summary>GS1-128 from (AI, value) pairs, in order; the human-readable text is the bracketed form.</summary>
    public static Barcode Gs1_128(params (string Ai, string Value)[] elements)
        => new(Symbology.Gs1_128, HumanReadable(elements), elements.Select(e => KeyValuePair.Create(e.Ai, e.Value)).ToList());

    /// <summary>GS1 DataMatrix from (AI, value) pairs, in order.</summary>
    public static Barcode Gs1DataMatrix(params (string Ai, string Value)[] elements)
        => new(Symbology.Gs1DataMatrix, HumanReadable(elements), elements.Select(e => KeyValuePair.Create(e.Ai, e.Value)).ToList());

    /// <summary>True for the two-dimensional symbologies.</summary>
    public bool IsMatrix => Symbology is Symbology.DataMatrix or Symbology.Gs1DataMatrix or Symbology.Qr;

    /// <summary>The modules of a linear symbol (true = bar), quiet zones not included.</summary>
    public bool[] LinearModules() => Symbology switch
    {
        Symbology.Code128 => Barcodes.Code128.Modules(Barcodes.Code128.Encode(Text)),
        Symbology.Gs1_128 => Barcodes.Code128.Modules(Barcodes.Code128.EncodeGs1(Elements!)),
        Symbology.Ean13 => Barcodes.Ean13.Modules(Text),
        _ => throw new InvalidOperationException($"{Symbology} is not a linear symbology."),
    };

    /// <summary>The modules of a matrix symbol (true = dark), quiet zone not included.</summary>
    public bool[,] MatrixModules()
    {
        switch (Symbology)
        {
            case Symbology.DataMatrix:
                return Barcodes.DataMatrix.Modules(Text);
            case Symbology.Gs1DataMatrix:
                return Barcodes.DataMatrix.Modules(Barcodes.DataMatrix.EncodeGs1(Elements!));
            case Symbology.Qr:
                using (var generator = new QRCodeGenerator())
                using (var data = generator.CreateQrCode(Text, QRCodeGenerator.ECCLevel.M))
                {
                    // QRCoder's matrix includes a 4-module quiet zone; drop it, the drawing adds its own margin.
                    var full = data.ModuleMatrix.Count;
                    var size = full - 8;
                    var modules = new bool[size, size];
                    for (var y = 0; y < size; y++)
                        for (var x = 0; x < size; x++)
                            modules[y, x] = data.ModuleMatrix[y + 4][x + 4];
                    return modules;
                }
            default:
                throw new InvalidOperationException($"{Symbology} is not a matrix symbology.");
        }
    }

    private static string HumanReadable((string Ai, string Value)[] elements)
        => string.Concat(elements.Select(e => $"({e.Ai}){e.Value}"));
}
