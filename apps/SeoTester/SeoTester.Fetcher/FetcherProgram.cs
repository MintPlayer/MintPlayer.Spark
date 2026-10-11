namespace SeoTester.Fetcher;

/// <summary>The seo-fetcher container's entry point (<see cref="FetcherHost"/>).</summary>
public static class FetcherProgram
{
    public static void Main(string[] args)
    {
        var app = FetcherHost.Build(FetcherHost.CreateBuilder(args));
        app.Run();
    }
}
