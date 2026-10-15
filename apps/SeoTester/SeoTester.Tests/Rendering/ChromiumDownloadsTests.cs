using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SeoTester.Rendering;

namespace SeoTester.Tests.Rendering;

/// <summary>
/// <see cref="ChromiumDownloads"/>, the router's "would Chromium save this navigation?" (<c>docs/seo_analyzer_PRD.md</c> §12,
/// "Downloads never reach Chromium"). Every case is a response measured in headless Chromium (Playwright 1.63's build,
/// 2026-10-15): one iframe per response, its download event the verdict. The predicate must agree with all of them, and
/// Chromium must still save exactly the ones it saved then (the media types aside: which of those it decodes is the build's).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class ChromiumDownloadsTests
{
    private sealed record Case(int Status, string? Type, string? Disposition, byte[] Body, bool Saved, bool NoSniff = false)
    {
        /// <summary>Audio, video and playlists: rendered or saved by what the build decodes.</summary>
        public bool Media => Type is { } t && (t.StartsWith("audio/", StringComparison.Ordinal) || t.StartsWith("video/", StringComparison.Ordinal)
            || t is "application/ogg" or "application/x-mpegurl" or "application/vnd.apple.mpegurl");

        public override string ToString() => $"{Status} type={Type ?? "(none)"} disposition={Disposition ?? "(none)"}{(NoSniff ? " nosniff" : "")} "
            + $"body={Convert.ToHexString(Body.AsSpan(0, Math.Min(Body.Length, 16)))}{(Body.Length > 16 ? $"…({Body.Length} bytes)" : "")}: Chromium {(Saved ? "saves" : "renders")} it";
    }

    private static byte[] T(string latin1) => Encoding.Latin1.GetBytes(latin1);
    private static byte[] X(string hex) => Convert.FromHexString(hex);
    private static byte[] At(int offset) => [.. T(new string('a', offset)), 0x00, 0x41];

    private static readonly byte[] Text = T("hello world, plain text here");
    private static readonly byte[] Html = T("<!doctype html><html><body><h1>x</h1></body></html>");
    private static readonly byte[] Binary = X("48490001024142");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly byte[] Pdf = T("%PDF-1.4\n%abc\n1 0 obj\n");
    private static readonly byte[] Xml = T("<a>x</a>");
    private static readonly byte[] Json = T("{\"a\":1}");

    private static Case Render(string? type, byte[] body, int status = 200, string? disposition = null, bool noSniff = false) => new(status, type, disposition, body, false, noSniff);
    private static Case Save(string? type, byte[] body, int status = 200, string? disposition = null, bool noSniff = false) => new(status, type, disposition, body, true, noSniff);

    private static readonly Case[] Cases =
    [
        // Status: only a 2xx is saved, 204 and 205 included; a redirect is followed, an error status shows an error page.
        Save("application/octet-stream", Binary, 201), Save("application/octet-stream", [], 204), Save("application/octet-stream", Binary, 205),
        Save("application/octet-stream", Binary, 206), Render(null, [], 204), Render("application/octet-stream", Binary, 302, "attachment"),
        Render("text/html", Html, 404, "attachment"), Render("application/octet-stream", Binary, 404), Render("application/zip", Binary, 500),

        // Content-Disposition: the token before the first ';' or ',', trimmed of spaces and tabs; anything but "inline" is saved.
        Save("text/html", Html, disposition: "attachment"), Save("text/html", Html, disposition: "ATTACHMENT; filename=\"x.html\""),
        Save("text/html", Html, disposition: "  attachment  ;filename=x"), Save("text/html", Html, disposition: "\tAttachment\t; x=y"),
        Save("text/html", Html, disposition: "attachment;"), Save("text/html", [], disposition: "attachment"),
        Save("text/html", Html, disposition: "attachment; filename=\"a,b\""), Save("text/html", Html, disposition: "attachment;filename=a,b"),
        Save("text/html", Html, disposition: "attachment, inline"), Save("text/html", Html, disposition: "foo"),
        Save("text/html", Html, disposition: "form-data; name=x"), Save("text/html", Html, disposition: "x-download"),
        Save("text/html", Html, disposition: "foo, inline"), Save("application/octet-stream", Binary, disposition: "inline"),
        Render("text/html", Html, disposition: "inline"), Render("text/html", Html, disposition: "INLINE"),
        Render("text/html", Html, disposition: "inline; filename=\"x.zip\""), Render("text/html", Html, disposition: "inline;filename=x"),
        Render("text/html", Html, disposition: "inline, attachment"), Render("text/html", Html, disposition: "inline, foo"),
        Render("text/html", Html, disposition: ""), Render("text/html", Html, disposition: "; filename=x.zip"),
        Render("text/html", Html, disposition: ", attachment"), Render("text/html", Html, disposition: "filename=x.zip"),
        Render("text/html", Html, disposition: "\"attachment\""), Render("text/html", Html, disposition: "attachment\tx"),
        Render("text/html", Html, disposition: "attachment filename=x"), Render("text/html", Html, disposition: "attach ment"),

        // Markup, XML and feeds; only the listed +xml types render.
        Render("text/html", Html), Render("text/html; charset=utf-8", Html), Render("TEXT/HTML", Html), Render("text/html", Binary),
        Render("application/xhtml+xml", T("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>x</body></html>")), Render("text/xml", Xml),
        Render("application/xml", Xml), Render("image/svg+xml", T("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")), Render("application/rss+xml", T("<rss/>")),
        Render("application/atom+xml", T("<feed/>")), Save("application/vnd.test+xml", Xml), Save("application/soap+xml", Xml), Save("application/xml-dtd", Text),

        // Text: all of text/* renders but Chromium's list of types it saves.
        Render("text/css", Text), Render("text/css", Binary), Render("text/javascript", Text), Render("text/markdown", Text), Render("text/x-unknown", Text),
        Render("text/json", Text), Render("text/event-stream", Text), Render("text/vtt", Text),
        Save("text/csv", Text), Save("text/calendar", Text), Save("text/vcard", Text), Save("text/rtf", Text), Save("text/tab-separated-values", Text),
        Save("text/x-calendar", Text), Save("text/x-vcalendar", Text), Save("text/vcalendar", Text), Save("text/x-vcard", Text), Save("text/directory", Text),
        Save("text/ldif", Text), Save("text/qif", Text), Save("text/x-qif", Text), Save("text/x-csv", Text), Save("text/x-vcf", Text),
        Save("text/comma-separated-values", Text), Save("text/tsv", Text), Save("text/ofx", Text), Save("text/vnd.sun.j2me.app-descriptor", Text),
        Save("text/x-ms-iqy", Text), Save("text/x-ms-odc", Text), Save("text/x-ms-rqy", Text), Save("text/x-ms-contact", Text),

        // JSON (and application/*+json) and script render.
        Render("application/json", Json), Render("application/json", Binary), Render("application/ld+json", Json), Render("application/vnd.api+json", Json),
        Save("application/json+x", Text), Render("application/javascript", Text), Render("application/x-javascript", Text),
        Render("application/ecmascript", Text), Render("application/x-ecmascript", Text),

        // Images: the formats Chromium decodes.
        Render("image/png", Png), Render("image/png", Binary), Render("image/jpeg", Png), Render("image/gif", Png), Render("image/webp", Png), Render("image/avif", Png),
        Render("image/bmp", Png), Render("image/x-icon", Png), Render("image/pjpeg", Png), Render("image/jpg", Png), Render("image/apng", Png),
        Render("image/vnd.microsoft.icon", Png), Render("image/x-xbitmap", Png), Render("image/x-png", Png),
        Save("image/tiff", Png), Save("image/heic", Png), Save("image/x-unknown", Png), Save("image/jxl", Png), Save("image/x-ms-bmp", Png),

        // Media: the containers the build decodes.
        Render("audio/mpeg", Binary), Render("audio/ogg", Binary), Render("audio/wav", Binary), Render("audio/webm", Binary), Render("audio/flac", Binary),
        Render("audio/mp4", Binary), Render("audio/x-m4a", Binary), Render("audio/aac", Binary), Render("audio/x-wav", Binary), Render("audio/mp3", Binary),
        Render("audio/x-mp3", Binary), Render("video/mp4", Binary), Render("video/webm", Binary), Render("video/ogg", Binary), Render("video/x-m4v", Binary),
        Render("video/x-matroska", Binary), Render("application/ogg", Binary), Render("application/x-mpegurl", Text), Render("application/vnd.apple.mpegurl", Text),
        Save("audio/x-unknown", Binary), Save("audio/wave", Binary), Save("audio/opus", Binary), Save("video/quicktime", Binary), Save("video/x-msvideo", Binary),
        Save("video/mp2t", Binary),

        // Everything else is saved, whatever the body.
        Save("application/pdf", Pdf), Save("application/octet-stream", Binary), Save("application/octet-stream", Html), Save("application/octet-stream", []),
        Save("application/octet-stream", Binary, noSniff: true), Save("application/zip", Binary), Save("application/wasm", Binary), Save("font/woff2", Binary),
        Save("multipart/related", Text), Save("message/rfc822", Text),

        // Plain text is sniffed for binary bytes in its first 1024 bytes, unless nosniff; a BOM or a known signature first keeps it text.
        Render("text/plain", Text), Save("text/plain", Binary), Render("text/plain", Binary, noSniff: true), Save("text/plain; charset=utf-8", Binary),
        Save("TEXT/PLAIN", Binary), Save(" text/plain ", Binary), Save("text/plain;", Binary), Render("text/plain", Pdf), Render("text/plain", X("48E9FF80410A")),
        Save("text/plain", At(500)), Save("text/plain", At(511)), Save("text/plain", At(512)), Save("text/plain", At(1000)), Save("text/plain", At(1023)),
        Render("text/plain", At(1024)), Render("text/plain", At(1100)), Render("text/plain", At(2000)),
        Render("text/plain", Png), Render("text/plain", T("GIF89a\x01\0")), Render("text/plain", T("GIF87a\x01\0")), Render("text/plain", X("FFD8FFE000104A46494600")),
        Render("text/plain", X("424D3A000000")), Render("text/plain", T("RIFF$\0\0\0WEBPVP8 \0")), Render("text/plain", X("1A45DFA39F42868101")),
        Render("text/plain", X("49443303000000000F")), Render("text/plain", T("#!\0\x01")), Render("text/plain", T("From\0\x01")),
        Render("text/plain", X("FFFE68006900")), Render("text/plain", X("FEFF00680069")), Render("text/plain", X("EFBBBF410042")),
        Render("text/plain", T("MZhello")), Render("text/plain", T("%!PS-Adobe-3.0")),
        Save("text/plain", X("0000010001001010")), Save("text/plain", T("OggS\0\x02\0\0")), Save("text/plain", T("<html>\0\x01")), Save("text/plain", T("PK\x03\x04")),

        // No usable type (missing, empty, malformed, */*): sniffed. Markup and BOMs render; known signatures decide; then binary bytes.
        Render(null, Text), Render(null, Html), Render(null, Png), Save(null, Binary), Render("", Text), Save("", Binary), Save("", Pdf),
        Save("text", Binary), Render("text", Html), Render("foo", Html), Render("*/*", Html), Save("*/*", Binary), Save("*/*", T("OggS\0\x02")),
        Render(null, Binary, noSniff: true), Render(null, Html, noSniff: true), Render("", Binary, noSniff: true), Render("*/*", Html, noSniff: true),
        Render(null, T("<?xml version=\"1.0\"?><a>x</a>")), Render(null, T("<html><body>hi\0\x01")), Render(null, T("<?xml version=\"1.0\"?><a>\0\x01")),
        Render(null, T("<!DOCTYPE html><p>x\0\x01")), Render(null, T(" \n\t<html>\0\x01")), Render(null, T("\f<html>\0")), Render(null, T("\r\n<html>\0")),
        Render(null, T("<DIV>x\0\x01")), Render(null, T("<!-- x -->\0\x01")), Render(null, T("<script>x\0\x01")), Render(null, T("<a href=x>\0\x01")),
        Render(null, T("<br>\0\x01")), Render(null, T("<htmlx>\0\x01")), Render(null, T("<p\0\x01")), Render(null, T("<H1>\0")), Render(null, T("<head>\0")),
        Render(null, T("<iframe\0")), Render(null, T("<font\0")), Render(null, T("<table\0")), Render(null, T("<style\0")), Render(null, T("<title\0")),
        Render(null, T("<body\0")), Render(null, T("<b\0")), Render(null, T("<!--\0")), Render(null, T("<?xml\0\x01")),
        Save(null, T("<foo>x\0\x01")), Save(null, T("<svg>x\0\x01")), Save(null, T("x<html>\0\x01")), Save(null, T("<h2>\0")), Save(null, T("<!doctype htm\0")),
        Save(null, T("<?XML\0")), Save(null, T("<?xm\0")),
        Render(null, X("FFFE68006900")), Render(null, X("FEFF00680069")), Render(null, X("EFBBBF410042")),
        Save(null, Pdf), Save(null, T("%!PS-Adobe-3.0\n%%Title: x\n")), Save(null, T("MZhello world")), Save(null, T("I I hello")), Save(null, T("II*hello")),
        Save(null, T("MM\0*")), Save(null, T(".RMFhello")), Save(null, X("D7CDC69A4142")), Save(null, T("Rar!\x1A\x07\0")), Save(null, X("504B03041400")),
        Save(null, X("1F8B0800")), Render(null, T("%PDF")), Render(null, T("%pdf-1.4")), Render(null, T("  %PDF-1.4")),
        Render(null, X("1A45DFA39F42868101")), Render(null, X("49443303000000000F")), Render(null, T("ID3\0")), Render(null, T("GIF89a\x01\0\x01\0\x80\0")),
        Render(null, X("FFD8FFE000104A46494600")), Render(null, X("424D3A000000")), Render(null, T("BMx\0")), Render(null, T("RIFF$\0\0\0WEBPVP8 \0")),
        Render(null, T("#!\0\x01")), Render(null, T("%!\0\x01")), Render(null, T("From\0\x01")), Render(null, T(">From\0\x01")), Save(null, T("from\0\x01")),
        Save(null, T("OggS\0\x02\0\0")), Save(null, T("RIFF$\0\0\0WAVEfmt \x10\0")), Save(null, T("\0\0\0\x18" + "ftypmp42\0\0")), Save(null, X("0000010001001010")),
        Save(null, T("GIF8\0")), Save(null, X("89504E470D0A1A")), Save(null, X("FFD80001")), Save(null, T("RIFF\0\0\0\0WEBPxyz\0")),
        Render(null, X("48E9FF80410A")), Render(null, X("481B244241")), Render(null, X("417F41")), Save(null, X("411A41")),
        Save(null, At(500)), Save(null, At(511)), Save(null, At(512)), Save(null, At(1000)), Save(null, At(1023)), Render(null, At(1024)),
        Render(null, At(1100)), Render(null, At(1444)), Render(null, At(1445)), Render(null, At(2000)),
        Render("unknown/unknown", Html), Render("unknown/unknown", Png), Render("unknown/unknown", T("<html>\0")), Save("unknown/unknown", Binary),
        Save("unknown/unknown", Binary, noSniff: true), Render("application/unknown", Html), Save("application/unknown", Binary), Save("application/unknown", Pdf),
        Save("application/unknown", T("%!PS-Adobe-2.0")), Save("application/unknown", Html, noSniff: true),
    ];

    [Fact]
    public void Every_measured_response_is_predicted_as_Chromium_handled_it()
    {
        Cases.Where(c => ChromiumDownloads.WouldDownload(c.Status, c.Type, c.Disposition, c.NoSniff ? "nosniff" : null, c.Body) != c.Saved)
            .Select(c => c.ToString()).Should().BeEmpty();
    }

    /// <summary>
    /// The cases again, in the Chromium this repository runs: still saved exactly where they were. Media types are left
    /// out (what Chromium decodes is the build's). Red the day a Playwright update changes what Chromium saves.
    /// </summary>
    [Fact]
    public async Task Headless_Chromium_still_saves_exactly_the_responses_predicted()
    {
        var cases = Cases.Where(c => !c.Media).ToList();
        await using var chromium = new ChromiumBrowser(Options.Create(new BrowserOptions { Mode = BrowserMode.Launch }), NullLogger<ChromiumBrowser>.Instance);
        var browser = await chromium.GetAsync(CancellationToken.None);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = false });
        var saved = new ConcurrentDictionary<int, byte>();
        try
        {
            await context.RouteAsync("**/*", route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                if (path.StartsWith("/frames/", StringComparison.Ordinal))
                {
                    var from = int.Parse(path["/frames/".Length..]);
                    var frames = string.Concat(Enumerable.Range(from, Math.Min(50, cases.Count - from)).Select(i => $"<iframe src=\"/case/{i}\"></iframe>"));
                    return route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = $"<!doctype html><html><body>{frames}</body></html>" });
                }
                if (!path.StartsWith("/case/", StringComparison.Ordinal))
                    return route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<p>landed</p>" });
                var c = cases[int.Parse(path["/case/".Length..])];
                var headers = new Dictionary<string, string>();
                if (c.Type is not null)
                    headers["content-type"] = c.Type;
                if (c.Disposition is not null)
                    headers["content-disposition"] = c.Disposition;
                if (c.NoSniff)
                    headers["x-content-type-options"] = "nosniff";
                if (c.Status is >= 300 and < 400)
                    headers["location"] = "/landing";
                return route.FulfillAsync(new() { Status = c.Status, Headers = headers, BodyBytes = c.Body });
            });
            // Fifty frames to a page, each page left open.
            for (var from = 0; from < cases.Count; from += 50)
            {
                var page = await context.NewPageAsync();
                page.Download += (_, download) =>
                {
                    var path = new Uri(download.Url).AbsolutePath;
                    if (path.StartsWith("/case/", StringComparison.Ordinal))
                        saved.TryAdd(int.Parse(path["/case/".Length..]), 0);
                };
                await page.GotoAsync($"https://downloads.test/frames/{from}");
            }
            // Every save is announced as its frame's response arrives: wait for as many as predicted (a failure bound only),
            // then a little longer for any that should not come.
            var expected = cases.Count(c => c.Saved);
            var watch = Stopwatch.StartNew();
            while (saved.Count < expected && watch.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(100);
            await Task.Delay(1000);
        }
        finally
        {
            await chromium.CloseAsync(context);
        }

        cases.Where((c, i) => saved.ContainsKey(i) != c.Saved).Select(c => c.ToString()).Should().BeEmpty();
    }
}
