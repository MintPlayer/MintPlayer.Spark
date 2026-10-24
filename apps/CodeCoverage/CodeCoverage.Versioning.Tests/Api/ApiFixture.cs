namespace CodeCoverage.Versioning.Tests.Api.ApiFixture;

// Read by DotnetApiListingTests through the assembly's metadata. Never used otherwise.
#pragma warning disable CS0169, CA1822, IDE0051

public class Widget
{
    public string Render(int depth = 2) => depth.ToString();
    protected virtual void OnRender() { }
    public int Size { get; protected set; }
    private int Secret;
    internal void Internals() { }
}

public sealed class SealedWidget : Widget
{
    protected override void OnRender() { }
}

public enum Size : byte { Small = 1, Large = 2 }
