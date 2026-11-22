# MintPlayer.Spark.Files.Images.SkiaSharp

Image processing for `Image` attributes, with SkiaSharp (MIT). This is the default choice.

```csharp
spark.AddFiles(files => files.UseSkiaSharpImages());
```

Every `Image` upload is processed during its commit, before the upload hooks and the malware scan:

- **The pixel cap comes first.** The dimensions are read from the header, and an image over
  `Spark:Files:MaxImagePixels` (default 40 million), or the attribute's own `fileOptions.maxPixels`, is refused before
  any pixel is decoded. This is the decompression-bomb guard.
- **Orientation.** The EXIF orientation is applied, so the stored image is upright and its width and height are as
  displayed.
- **Re-encoding is the sanitiser.** The image is never stored as it was uploaded. EXIF (GPS included), XMP, comments and
  anything appended after the image data are gone. JPEG stays JPEG and WebP stays WebP (quality 90). PNG stays PNG.
  A GIF becomes a PNG of its first frame, and its file name gets a `.png` extension.
- **Variants** are WebP (quality 80), made from the one decoded bitmap and never upscaled. By default these are
  `thumb` (160 px) and `medium` (800 px). An attribute changes them with `fileOptions.variants` (`{}` turns them off),
  and an app changes the default with `Spark:Files:DefaultImageVariants`.
- **One instance decodes at most `Spark:Files:MaxConcurrentImageProcessing` images at once** (default 2). An image at the
  cap takes about 160 MB to decode.

An image the processor cannot read is refused, with "The image could not be read.".

**Linux containers need no OS packages.** This package references `SkiaSharp.NativeAssets.Linux.NoDependencies`, which
carries `libSkiaSharp.so` with no fontconfig dependency. That was measured on `aspnet:11.0`, the chiseled image and
Alpine (publish for `linux-musl-x64`). Don't add the plain `SkiaSharp` package on its own: it has no Linux native.

The alternative is `MintPlayer.Spark.Files.Images.NetVips` (libvips, whose natives are LGPL). Both pass the same
conformance suite.
