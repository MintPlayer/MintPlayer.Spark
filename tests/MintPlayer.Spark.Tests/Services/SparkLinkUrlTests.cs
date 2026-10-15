using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The one rule for a stored link a browser later follows (a program unit's url, a notification's url): an app path
/// or an absolute http(s) URL with a host. Each refused value is one a browser would read as another host, another
/// scheme, or a deception.
/// </summary>
public class SparkLinkUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/runs/42")]
    [InlineData("/merge-queue/setup?provider=github&account=acme#permissions")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/docs?tab=1#top")]
    [InlineData("http://example.com:8080/x")]
    [InlineData("HTTPS://EXAMPLE.COM/x")]
    [InlineData("https://[::1]/x")]
    public void Allows_an_app_path_or_an_absolute_http_url(string url)
        => SparkLinkUrl.IsAllowed(url).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("//evil.example/x")]                 // protocol-relative: another host
    [InlineData("/\\evil.example/x")]                // a browser reads "\" as "/"
    [InlineData("/\t/evil.example/x")]               // a browser strips the tab: "//evil.example"
    [InlineData("/\n/evil.example/x")]
    [InlineData(" /runs/42")]
    [InlineData("/runs/42 ")]
    [InlineData("/runs/4\u007f2")]                   // a control character (DEL: test names travel as XML, NUL cannot)
    [InlineData("https://exa mple.com/")]
    [InlineData("runs/42")]                          // relative: resolves against whatever page is open
    [InlineData("./runs/42")]
    [InlineData("?tab=1")]
    [InlineData("#top")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("vbscript:x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https:example.com")]                // a browser reads https://example.com; no authority on the text
    [InlineData("https:///example.com")]
    [InlineData("https://")]
    [InlineData("https://github.com@evil.example/")] // reads as github.com, goes to evil.example
    [InlineData("https://@evil.example/")]
    public void Refuses_everything_else(string? url)
        => SparkLinkUrl.IsAllowed(url).Should().BeFalse();

    [Theory]
    [InlineData("/runs/42", true)]
    [InlineData("/", true)]
    [InlineData("https://example.com/runs/42", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\t/evil.example/x", false)]
    [InlineData(null, false)]
    public void An_app_path_is_one_leading_slash_and_nothing_a_browser_reads_as_a_host(string? url, bool expected)
        => SparkLinkUrl.IsAppPath(url).Should().Be(expected);
}
