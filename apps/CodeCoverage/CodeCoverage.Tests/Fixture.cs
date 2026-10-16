namespace CodeCoverage.Tests;

/// <summary>
/// Reads a real tool-produced coverage report from <c>Fixtures/</c>.
///
/// These are committed on purpose. The reports under <c>CodeCoverage.Tests/coverage/</c>
/// are real too, but <c>.gitignore</c> matches <c>**/coverage/</c>, so they are local
/// build artifacts that CI never sees — a test bound to them would pass here and
/// assert nothing there.
/// </summary>
public static class Fixture
{
    public static string Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        // Loud on purpose: a fixture that silently fails to copy would turn every
        // test built on it into a no-op, which is how #423 shipped.
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture '{name}' was not copied to the test output at '{path}'. " +
                "Check the Content item in CodeCoverage.Tests.csproj.", path);

        return File.ReadAllText(path);
    }

    /// <summary>
    /// A raw HTTP capture (<c>curl -i</c> / <c>gh api -i</c>: status line, headers, blank line, body) as the response it
    /// was, headers included, so an adapter classifies exactly what GitHub sent. A trailing <c>gh: …</c> line, which the
    /// CLI appends to an error body, is dropped.
    /// </summary>
    public static HttpResponseMessage Http(string name)
    {
        var raw = Read(name).Replace("\r\n", "\n", StringComparison.Ordinal);
        var split = raw.IndexOf("\n\n", StringComparison.Ordinal);
        var head = raw[..split].Split('\n');
        var body = raw[(split + 2)..];
        var ghLine = body.LastIndexOf("gh: ", StringComparison.Ordinal);
        if (ghLine >= 0 && body.LastIndexOf('}') < ghLine)
            body = body[..ghLine];

        var status = (System.Net.HttpStatusCode)int.Parse(head[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var response = new HttpResponseMessage(status) { Content = new StringContent(body.Trim(), System.Text.Encoding.UTF8, "application/json") };
        foreach (var line in head.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var (header, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
            // The content is rebuilt from the body; its captured length and type would only disagree with it.
            if (header.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!response.Headers.TryAddWithoutValidation(header, value))
                response.Content.Headers.TryAddWithoutValidation(header, value);
        }
        return response;
    }
}
