using MintPlayer.Spark.Abstractions;

namespace SparkEditor.LookupReferences;

public sealed class AttributeDataType : TransientLookupReference<string>
{
    private AttributeDataType() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<AttributeDataType> Items { get; } =
    [
        new() { Key = "string", Values = _TS("String") },
        new() { Key = "number", Values = _TS("Number", nl: "Geheel getal") },
        new() { Key = "decimal", Values = _TS("Decimal", nl: "Decimaal") },
        new() { Key = "boolean", Values = _TS("Boolean", nl: "Booleaans") },
        new() { Key = "datetime", Values = _TS("Date/Time", nl: "Datum/Tijd") },
        new() { Key = "date", Values = _TS("Date", nl: "Datum") },
        new() { Key = "guid", Values = _TS("GUID") },
        new() { Key = "color", Values = _TS("Color", nl: "Kleur") },
        new() { Key = "translatedString", Values = _TS("Translated String", nl: "Vertaalde tekst") },
        new() { Key = "Reference", Values = _TS("Reference", nl: "Referentie") },
        new() { Key = "AsDetail", Values = _TS("As Detail") },
    ];
}
