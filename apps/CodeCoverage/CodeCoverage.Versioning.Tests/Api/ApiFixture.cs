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

/// <summary>Every constant type, events, constrained generics and the signature shapes the listing names.</summary>
public class Shapes<TOut, TIn>
    where TOut : class, IComparable<TOut>
    where TIn : struct
{
    public const char Letter = 'x';
    public const sbyte Signed = -1;
    public const short Short = -2;
    public const ushort UShort = 3;
    public const uint UInt = 4;
    public const ulong ULong = 5;
    public const float Single = 1.5f;
    public const double Double = 2.25;
    public const string Text = "hi";
    public const bool Flag = true;

    public event EventHandler? Changed;
    protected event EventHandler? Guarded;
    private event EventHandler? Hidden;

    public int[,] Grid(int[,] cells) => cells;
    public nint Native(nuint size) => (nint)size;
    public void Read(in int value, ref int target, out int result) => result = target = value;
    public TOut Echo(TOut value, TIn other) => value;
    public TM Generic<TM>(TM value) where TM : new() => value;
    public Environment.SpecialFolder Folder(Environment.SpecialFolder folder) => folder;
    public object? Nothing(object? value = null) => value;
}

public interface IVariant<out TResult, in TArg> { TResult Run(TArg arg); }
