# MintPlayer.Spark.Files.Images.NetVips

Image processing for `Image` attributes with libvips, through NetVips.

```csharp
spark.AddFiles(files => files.UseNetVipsImages());
```

It does exactly what `MintPlayer.Spark.Files.Images.SkiaSharp` does, and both pass the same conformance suite:

- the pixel cap is read from the header before anything is decoded;
- the EXIF orientation is applied;
- the image is re-encoded with no metadata (`keep: none`), and a GIF becomes a PNG of its first frame;
- WebP variants are made from the one decoded image.

The README of the SkiaSharp package has the details and the settings. libvips is the faster of the two on large images
(PRD SP5).

## Licence

This package is MIT. The libvips binaries it pulls in (`NetVips.Native`) are **LGPL-2.1**, dynamically linked. An app
that ships them must allow those libraries to be replaced. That is the reason this is a separate package and not the
default.

## Hardening

- Loaders that libvips marks untrusted are blocked (`BlockUntrusted`). That covers ImageMagick, PDF, SVG and others.
  The loader that opened each upload must also match the type the sniffer detected.
- The libvips operation cache is off (`Cache.Max = 0`), so no upload's pixels stay in memory after it is processed.

**Linux containers need no OS packages.** `NetVips.Native` carries `libvips.so.42` and its dependencies per RID. That was
measured on `aspnet:11.0`, the chiseled image and Alpine.
