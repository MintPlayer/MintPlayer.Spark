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
}
