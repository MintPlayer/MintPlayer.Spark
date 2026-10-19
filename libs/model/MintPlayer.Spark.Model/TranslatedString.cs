using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Spark's one name system (commerce X17, reference-data RD7): app texts, labels, messages, and the
/// names of places, all in any language.
/// <para>
/// A value is an ordered list of <see cref="TranslatedStringEntry"/>: a text plus its RFC 5646
/// <see cref="TranslatedStringEntry.Tag"/>, and for names optionally a <see cref="TranslatedStringKind"/>,
/// a validity window, a source and a romanization origin. An app text is the simple case, one plain entry
/// per language: <c>TranslatedString.Of(("en", "Save"), ("nl", "Opslaan"))</c>.
/// </para>
/// <para>
/// <b>One shape, everywhere</b> — the API (System.Text.Json), RavenDB (Newtonsoft, through
/// <c>TranslatedStringNewtonsoftJsonConverter</c> in MintPlayer.Spark) and the client all use:
/// <code>{"Entries":[{"Tag":"en","Value":"Save"},{"Tag":"nl","Value":"Opslaan","Kind":"official"}]}</code>
/// Member names are the CLR names on purpose: RavenDB's LINQ translator addresses stored JSON by CLR
/// member name, so a generated index's <c>x.Description.Entries.Where(e =&gt; e.Tag == "nl")</c> only
/// works when the stored names are exactly these. Optional members are omitted when null; tags are
/// stored in canonical case (<see cref="LanguageTag.Canonicalize"/>).
/// </para>
/// <para>
/// Two input-only shorthands are read, never written: a JSON string is a translation <see cref="Key"/>
/// (App_Data files), and a flat object <c>{"en":"Save","nl":"Opslaan"}</c> is one plain entry per
/// member (<c>translations.json</c> leaves).
/// </para>
/// </summary>
[JsonConverter(typeof(TranslatedStringJsonConverter))]
public sealed class TranslatedString : IEquatable<TranslatedString>
{
    /// <summary>
    /// The language every Spark text is authored in, and the last language of the fallback chain before
    /// "the first current entry" (<see cref="Resolve"/>).
    /// </summary>
    public const string DefaultLanguage = "en";

    /// <summary>The entries, in author order: within one tag the first current entry wins.</summary>
    public List<TranslatedStringEntry> Entries { get; set; } = new();

    /// <summary>
    /// A translation KEY, set when the JSON held a plain string instead of an object (#467, D1/D4):
    /// App_Data files refer to <c>translations.json</c> by key and never embed translated text.
    /// The loader resolves such a value through <c>SparkText</c> (MintPlayer.Spark.Abstractions) into one carrying
    /// <see cref="Entries"/>; an unresolved one serializes back to its key. Never stored.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>A reference to a <c>translations.json</c> key, not yet resolved.</summary>
    public static TranslatedString FromKey(string key) => new() { Key = key };

    /// <summary>An app text in one language: <c>TranslatedString.Of("en", "Save")</c>.</summary>
    public static TranslatedString Of(string tag, string value) => new TranslatedString().Set(tag, value);

    /// <summary>
    /// An app text in several languages, one plain entry each:
    /// <c>TranslatedString.Of(("en", "Save"), ("fr", "Enregistrer"), ("nl", "Opslaan"))</c>.
    /// </summary>
    public static TranslatedString Of(params ReadOnlySpan<(string Tag, string Value)> texts)
    {
        var ts = new TranslatedString();
        foreach (var (tag, value) in texts) ts.Set(tag, value);
        return ts;
    }

    /// <summary>Whether there is no entry at all.</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>The distinct tags, in first-appearance order.</summary>
    public IEnumerable<string> Tags => Entries.Select(e => e.Tag).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The plain text of exactly <paramref name="tag"/> (no fallback), or null. Setting replaces the tag's
    /// plain entries (see <see cref="Set"/>); setting null removes them. Names with a kind or validity
    /// are left alone either way.
    /// </summary>
    public string? this[string tag]
    {
        get => Entries.FirstOrDefault(e => e.IsPlain && LanguageTag.AreEqual(e.Tag, tag))?.Value;
        set
        {
            if (value is null) Entries.RemoveAll(e => e.IsPlain && LanguageTag.AreEqual(e.Tag, tag));
            else Set(tag, value);
        }
    }

    /// <summary>
    /// Sets the plain text of <paramref name="tag"/>: the first plain entry of that tag is replaced in
    /// place (so the author order holds), any further plain ones are dropped, and when there is none the
    /// entry is appended. Returns this value, for chaining.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is not a well-formed RFC 5646 tag.</exception>
    public TranslatedString Set(string tag, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var entry = new TranslatedStringEntry(tag, value);
        var first = Entries.FindIndex(e => e.IsPlain && LanguageTag.AreEqual(e.Tag, entry.Tag));
        if (first < 0)
        {
            Entries.Add(entry);
            return this;
        }

        Entries[first] = entry;
        for (var i = Entries.Count - 1; i > first; i--)
            if (Entries[i].IsPlain && LanguageTag.AreEqual(Entries[i].Tag, entry.Tag)) Entries.RemoveAt(i);
        return this;
    }

    /// <summary>The text for <paramref name="language"/> today, through the fallback chain of <see cref="Resolve"/>.</summary>
    public string GetValue(string language) => Resolve([language]);

    /// <summary>
    /// The fallback chain, identical on the server and in <c>resolveTranslation</c> on the client:
    /// <list type="number">
    ///   <item>for each requested range in order, then <paramref name="defaultLanguage"/>: the RFC 4647
    ///   lookup chain of that range (<see cref="LanguageTag.LookupChain"/>), and for each tag in it the
    ///   first <em>current</em> entry with that tag (tags compare case-insensitively);</item>
    ///   <item>otherwise the first current entry;</item>
    ///   <item>otherwise the first entry; otherwise the empty string.</item>
    /// </list>
    /// An entry is current at <paramref name="asOf"/> (default: today, UTC) when it is not
    /// <see cref="TranslatedStringKind.Historic"/> and its validity window holds the day.
    /// </summary>
    public string Resolve(IEnumerable<string?> languageRanges, string? defaultLanguage = DefaultLanguage, DateOnly? asOf = null)
    {
        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var range in languageRanges.Append(defaultLanguage))
            foreach (var candidate in LanguageTag.LookupChain(range))
                foreach (var entry in Entries)
                    if (entry.IsCurrent(day) && LanguageTag.AreEqual(entry.Tag, candidate))
                        return entry.Value;

        foreach (var entry in Entries)
            if (entry.IsCurrent(day)) return entry.Value;

        return Entries.Count > 0 ? Entries[0].Value : string.Empty;
    }

    /// <summary>A deep copy (entries are immutable records, so copying the list is enough).</summary>
    public TranslatedString Clone() => new() { Key = Key, Entries = [.. Entries] };

    /// <summary>
    /// A new value with <paramref name="transform"/> applied to every entry's text, keeping tags, kinds
    /// and validity: <c>template.Map((tag, text) =&gt; string.Format(culture(tag), text, args))</c>.
    /// </summary>
    public TranslatedString Map(Func<string, string, string> transform)
        => new() { Entries = Entries.Select(e => e with { Value = transform(e.Tag, e.Value) }).ToList() };

    /// <summary>Value equality: the same key and the same entries in the same order.</summary>
    public bool Equals(TranslatedString? other)
        => other is not null
        && (ReferenceEquals(this, other) || (Key == other.Key && Entries.SequenceEqual(other.Entries)));

    public override bool Equals(object? obj) => Equals(obj as TranslatedString);

    /// <remarks>The value is mutable: do not change one while it is a key in a hash set or dictionary.</remarks>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key);
        foreach (var entry in Entries) hash.Add(entry);
        return hash.ToHashCode();
    }

    public override string ToString() => Key ?? GetValue(DefaultLanguage);
}

/// <summary>
/// One text of a <see cref="TranslatedString"/>. An app text uses only <see cref="Tag"/> and
/// <see cref="Value"/> (a <em>plain</em> entry); a place name may add the rest.
/// </summary>
public sealed record TranslatedStringEntry
{
    public TranslatedStringEntry() { }

    /// <param name="tag">An RFC 5646 tag; stored in canonical case.</param>
    /// <param name="value">The text.</param>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is not well-formed.</exception>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public TranslatedStringEntry(string tag, string value)
    {
        Tag = LanguageTag.Canonicalize(tag);
        Value = value;
    }

    /// <summary>The RFC 5646 language tag, canonical case (<c>nl</c>, <c>zh-Hant</c>, <c>sr-Latn</c>).</summary>
    public required string Tag { get; init; }

    public required string Value { get; init; }

    /// <summary>For names: official, common, exonym or historic. Null for an app text.</summary>
    public TranslatedStringKind? Kind { get; init; }

    /// <summary>First day the name applies (inclusive). Null: since always.</summary>
    public DateOnly? ValidFrom { get; init; }

    /// <summary>First day the name no longer applies (exclusive), so a rename on day D closes the old name with <c>ValidTo = D</c> and opens the new one with <c>ValidFrom = D</c>. Null: still valid.</summary>
    public DateOnly? ValidTo { get; init; }

    /// <summary>Where the name comes from (a register, GNS, Wikidata, …). Free text.</summary>
    public string? Source { get; init; }

    /// <summary>Set on a romanization (e.g. tag <c>ru-Latn</c>): whether a source published it or it was generated.</summary>
    public RomanizationOrigin? Romanization { get; init; }

    /// <summary>An app text: no kind and no validity window.</summary>
    public bool IsPlain => Kind is null && ValidFrom is null && ValidTo is null;

    /// <summary>Not historic, and <paramref name="day"/> lies in <c>[ValidFrom, ValidTo)</c>.</summary>
    public bool IsCurrent(DateOnly day)
        => Kind != TranslatedStringKind.Historic
        && (ValidFrom is null || ValidFrom.Value <= day)
        && (ValidTo is null || day < ValidTo.Value);
}

/// <summary>What kind of name an entry is (RD7). Serialized lowercase: <c>official</c>, <c>common</c>, …</summary>
public enum TranslatedStringKind
{
    /// <summary>The name an authority (a register, a statute) gives.</summary>
    Official,
    /// <summary>The name in everyday use ("Den Haag" beside the official "'s-Gravenhage").</summary>
    Common,
    /// <summary>A name used outside the area in another language ("Anvers" for Antwerpen).</summary>
    Exonym,
    /// <summary>A former name: still searchable, never displayed as current.</summary>
    Historic,
}

/// <summary>Where a romanization comes from (RD7). Serialized lowercase.</summary>
public enum RomanizationOrigin
{
    /// <summary>Published by a source (the register, GNS, Wikidata).</summary>
    Authoritative,
    /// <summary>Produced by transliteration.</summary>
    Generated,
}

/// <summary>
/// System.Text.Json converter for <see cref="TranslatedString"/>; the shape is documented on the type.
/// <c>TranslatedStringNewtonsoftJsonConverter</c> (MintPlayer.Spark) writes and reads the same shape
/// through <see cref="TranslatedStringJson"/>, so the API and RavenDB cannot drift apart.
/// </summary>
public class TranslatedStringJsonConverter : JsonConverter<TranslatedString>
{
    public override TranslatedString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return TranslatedString.FromKey(reader.GetString()!);
            case JsonTokenType.StartObject:
                break;
            default:
                throw new JsonException("Expected a translation key or an object for TranslatedString");
        }

        var ts = new TranslatedString();
        var sawEntries = false;
        var sawFlat = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ts;

            var name = reader.GetString()!;
            reader.Read();
            if (reader.TokenType == JsonTokenType.StartArray && string.Equals(name, TranslatedStringJson.EntriesName, StringComparison.OrdinalIgnoreCase))
            {
                sawEntries = true;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                        throw new JsonException("A TranslatedString entry must be an object");
                    var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var field = reader.GetString()!;
                        reader.Read();
                        fields[field] = reader.TokenType switch
                        {
                            JsonTokenType.Null => null,
                            JsonTokenType.String => reader.GetString(),
                            _ => throw new JsonException($"TranslatedString entry member '{field}' must be a string"),
                        };
                    }
                    if (TranslatedStringJson.EntryFrom(fields) is { } entry) ts.Entries.Add(entry);
                }
            }
            else if (reader.TokenType is JsonTokenType.String or JsonTokenType.Null)
            {
                sawFlat = true;
                var text = reader.GetString();
                if (text is not null) ts.Entries.Add(TranslatedStringJson.PlainEntry(name, text));
            }
            else
            {
                throw new JsonException($"TranslatedString member '{name}' must be \"Entries\": [...] or a language tag with a text");
            }

            if (sawEntries && sawFlat)
                throw new JsonException("A TranslatedString is either {\"Entries\":[...]} or a flat {\"<tag>\":\"text\"} object, not both");
        }

        throw new JsonException("Unexpected end of JSON for TranslatedString");
    }

    public override void Write(Utf8JsonWriter writer, TranslatedString value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Key is not null && value.Entries.Count == 0)
        {
            writer.WriteStringValue(value.Key);
            return;
        }

        writer.WriteStartObject();
        writer.WriteStartArray(TranslatedStringJson.EntriesName);
        foreach (var entry in value.Entries)
        {
            writer.WriteStartObject();
            foreach (var (field, text) in TranslatedStringJson.FieldsOf(entry))
                writer.WriteString(field, text);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

/// <summary>
/// The field-level codec both JSON converters share, so the System.Text.Json (API) and Newtonsoft
/// (RavenDB) forms are one shape by construction. Every member is a JSON string.
/// </summary>
public static class TranslatedStringJson
{
    public const string EntriesName = nameof(TranslatedString.Entries);

    /// <summary>The members to write, in order, omitting nulls.</summary>
    public static IEnumerable<(string Name, string Value)> FieldsOf(TranslatedStringEntry entry)
    {
        yield return (nameof(TranslatedStringEntry.Tag), entry.Tag);
        yield return (nameof(TranslatedStringEntry.Value), entry.Value);
        if (entry.Kind is { } kind) yield return (nameof(TranslatedStringEntry.Kind), kind.ToString().ToLowerInvariant());
        if (entry.ValidFrom is { } from) yield return (nameof(TranslatedStringEntry.ValidFrom), from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (entry.ValidTo is { } to) yield return (nameof(TranslatedStringEntry.ValidTo), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (entry.Source is { } source) yield return (nameof(TranslatedStringEntry.Source), source);
        if (entry.Romanization is { } origin) yield return (nameof(TranslatedStringEntry.Romanization), origin.ToString().ToLowerInvariant());
    }

    /// <summary>An entry from its members (names case-insensitive); null when it has no value.</summary>
    /// <exception cref="JsonException">A member is unknown or malformed.</exception>
    public static TranslatedStringEntry? EntryFrom(IReadOnlyDictionary<string, string?> fields)
    {
        string? Field(string name) => fields.TryGetValue(name, out var v) ? v : null;

        foreach (var name in fields.Keys)
            if (!KnownFields.Contains(name))
                throw new JsonException($"Unknown TranslatedString entry member '{name}'");

        var tag = Field(nameof(TranslatedStringEntry.Tag));
        var value = Field(nameof(TranslatedStringEntry.Value));
        if (value is null) return null;
        if (!LanguageTag.IsWellFormed(tag))
            throw new JsonException($"'{tag}' is not a well-formed RFC 5646 language tag");

        return new TranslatedStringEntry(tag!, value)
        {
            Kind = ParseEnum<TranslatedStringKind>(Field(nameof(TranslatedStringEntry.Kind)), nameof(TranslatedStringEntry.Kind)),
            ValidFrom = ParseDate(Field(nameof(TranslatedStringEntry.ValidFrom)), nameof(TranslatedStringEntry.ValidFrom)),
            ValidTo = ParseDate(Field(nameof(TranslatedStringEntry.ValidTo)), nameof(TranslatedStringEntry.ValidTo)),
            Source = Field(nameof(TranslatedStringEntry.Source)),
            Romanization = ParseEnum<RomanizationOrigin>(Field(nameof(TranslatedStringEntry.Romanization)), nameof(TranslatedStringEntry.Romanization)),
        };
    }

    /// <summary>A plain entry from the flat shorthand <c>{"tag":"text"}</c>.</summary>
    /// <exception cref="JsonException">The member name is not a well-formed tag.</exception>
    public static TranslatedStringEntry PlainEntry(string tag, string text)
        => LanguageTag.IsWellFormed(tag)
            ? new TranslatedStringEntry(tag, text)
            : throw new JsonException($"'{tag}' is not a well-formed RFC 5646 language tag");

    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(TranslatedStringEntry.Tag), nameof(TranslatedStringEntry.Value), nameof(TranslatedStringEntry.Kind),
        nameof(TranslatedStringEntry.ValidFrom), nameof(TranslatedStringEntry.ValidTo),
        nameof(TranslatedStringEntry.Source), nameof(TranslatedStringEntry.Romanization),
    };

    private static T? ParseEnum<T>(string? text, string member) where T : struct, Enum
        => text is null ? null
            : Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && text.Length > 0 && !char.IsAsciiDigit(text[0]) && text[0] != '-'
                ? parsed
                : throw new JsonException($"'{text}' is not a valid TranslatedString entry {member}");

    private static DateOnly? ParseDate(string? text, string member)
        => text is null ? null
            : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : throw new JsonException($"'{text}' is not a yyyy-MM-dd {member}");
}
