namespace DemoApp.Library.VirtualObjects;

/// <summary>
/// Marker class for the <c>StartPage</c> Virtual PO — the composed landing page the "Start"
/// program unit opens. Spark's <c>EntityTypeDefinition.ClrType</c> is required, so every schema
/// registration needs a CLR type to resolve against; this class exists only to satisfy that
/// shape. No persistence and no context root: every instance the client ever sees is composed
/// in <c>StartPageActions.OnComposeAsync</c>, which ignores the requested id.
/// </summary>
public sealed class StartPage
{
    /// <summary>The greeting text, composed per request.</summary>
    public string? Welcome { get; set; }

    /// <summary>Live count of Person documents.</summary>
    public int PeopleCount { get; set; }

    /// <summary>Live count of Company documents.</summary>
    public int CompanyCount { get; set; }

    /// <summary>Live count of Car documents.</summary>
    public int CarCount { get; set; }
}
