using QRCoder;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Renders the authenticator-enrollment QR code as SVG on the server (#460, D16; spike SP-D).
/// </summary>
/// <remarks>
/// <para>
/// Server-side so the client needs no QR library and the shared secret never passes through a
/// third-party script. Uses QRCoder (MIT, no dependencies) rather than an in-house encoder: QR
/// encoding (mode selection, Reed–Solomon, masking) is a large, well-specified algorithm with nothing
/// application-specific about it. Error correction level M, the level authenticator enrollment codes
/// conventionally use.
/// </para>
/// </remarks>
public sealed class SparkQrCodeRenderer
{
    /// <summary>An SVG document encoding <paramref name="text"/> (dark modules on a white background, 4-module quiet zone).</summary>
    public string RenderSvg(string text)
    {
        using var data = Encode(text);
        using var svg = new SvgQRCode(data);
        return svg.GetGraphic(pixelsPerModule: 4);
    }

    /// <summary>The module matrix <see cref="RenderSvg"/> draws, quiet zone included (<c>true</c> = dark).</summary>
    public bool[,] RenderModules(string text)
    {
        using var data = Encode(text);
        var size = data.ModuleMatrix.Count;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                modules[y, x] = data.ModuleMatrix[y][x];
        return modules;
    }

    private static QRCodeData Encode(string text)
    {
        using var generator = new QRCodeGenerator();
        return generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
    }
}
