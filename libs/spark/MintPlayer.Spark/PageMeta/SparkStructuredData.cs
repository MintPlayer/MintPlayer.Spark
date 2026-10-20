using System.Globalization;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.PageMeta;

/// <summary>
/// A language version of a page (commerce PRD X22, GB9): <c>&lt;link rel="alternate" hreflang="…" href="…"&gt;</c> in
/// <c>index.html</c> and <c>xhtml:link</c> in the sitemap.
/// </summary>
/// <param name="Language">A BCP 47 tag (<c>nl</c>, <c>nl-BE</c>) or <c>x-default</c>.</param>
/// <param name="Href">The version's URL: absolute, or a path resolved against the public origin.</param>
public sealed record SparkHreflang(string Language, string Href)
{
    /// <summary>The fallback version for languages no alternate matches.</summary>
    public const string XDefault = "x-default";

    /// <summary>A language tag the way hreflang accepts it: letters, digits and dashes, or <c>x-default</c>.</summary>
    internal bool IsValid => Language.Length is > 0 and <= 35
        && Language.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && char.IsAsciiLetter(Language[0]);
}

/// <summary>
/// schema.org structured data for a page, written into <c>index.html</c> as <c>&lt;script type="application/ld+json"&gt;</c>
/// by the page-meta middleware (commerce PRD X22, GB9). Use <see cref="SparkEventData"/>, <see cref="SparkProductData"/> or
/// <see cref="Raw"/>.
/// </summary>
public abstract record SparkStructuredData
{
    /// <summary>The JSON-LD object, with <c>@context</c> and <c>@type</c>.</summary>
    public abstract JsonObject ToJsonLd();

    /// <summary>Any other schema.org type, written as given (add <c>@context</c> and <c>@type</c> yourself).</summary>
    public static SparkStructuredData Raw(JsonObject jsonLd) => new RawData(jsonLd);

    private sealed record RawData(JsonObject JsonLd) : SparkStructuredData
    {
        public override JsonObject ToJsonLd() => (JsonObject)JsonLd.DeepClone();
    }

    internal const string Context = "https://schema.org";

    /// <summary>An ISO 8601 date-time that keeps its offset: an event's start "in the venue's offset" (§4.14).</summary>
    internal static string IsoDateTime(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    internal static void Set(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[name] = value;
    }

    internal static void SetArray(JsonObject target, string name, IEnumerable<string>? values)
    {
        var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (list is { Count: > 0 })
            target[name] = new JsonArray(list.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    }
}

/// <summary>schema.org <c>EventStatusType</c>.</summary>
public enum SparkEventStatus { Scheduled, Cancelled, Postponed, Rescheduled, MovedOnline }

/// <summary>schema.org <c>EventAttendanceModeEnumeration</c>.</summary>
public enum SparkEventAttendanceMode { Offline, Online, Mixed }

/// <summary>schema.org <c>ItemAvailability</c>.</summary>
public enum SparkAvailability { InStock, OutOfStock, SoldOut, PreOrder, PreSale, LimitedAvailability, BackOrder, Discontinued, OnlineOnly, InStoreOnly }

/// <summary>A venue: schema.org <c>Place</c> with a <c>PostalAddress</c>.</summary>
/// <param name="Country">ISO 3166-1 alpha-2 (<c>BE</c>).</param>
public sealed record SparkPlace(string Name, string? StreetAddress = null, string? Locality = null, string? PostalCode = null,
    string? Region = null, string? Country = null)
{
    internal JsonObject ToJsonLd()
    {
        var address = new JsonObject { ["@type"] = "PostalAddress" };
        SparkStructuredData.Set(address, "streetAddress", StreetAddress);
        SparkStructuredData.Set(address, "addressLocality", Locality);
        SparkStructuredData.Set(address, "postalCode", PostalCode);
        SparkStructuredData.Set(address, "addressRegion", Region);
        SparkStructuredData.Set(address, "addressCountry", Country);
        return new JsonObject { ["@type"] = "Place", ["name"] = Name, ["address"] = address };
    }
}

/// <summary>An offer: schema.org <c>Offer</c>. <paramref name="Price"/> is the price the buyer pays (<c>FinalPrice</c>, commerce §4.4).</summary>
public sealed record SparkOffer(Money Price, SparkAvailability Availability = SparkAvailability.InStock, string? Url = null,
    string? Name = null, DateTimeOffset? ValidFrom = null, DateTimeOffset? ValidThrough = null)
{
    internal JsonObject ToJsonLd()
    {
        var offer = new JsonObject
        {
            ["@type"] = "Offer",
            ["price"] = Price.Amount.ToString(CultureInfo.InvariantCulture),
            ["priceCurrency"] = Price.Currency,
            ["availability"] = SparkStructuredData.Context + "/" + Availability,
        };
        SparkStructuredData.Set(offer, "name", Name);
        SparkStructuredData.Set(offer, "url", Url);
        if (ValidFrom is { } from)
            offer["validFrom"] = SparkStructuredData.IsoDateTime(from);
        if (ValidThrough is { } through)
            offer["validThrough"] = SparkStructuredData.IsoDateTime(through);
        return offer;
    }
}

/// <summary>An organisation (organizer, brand owner): schema.org <c>Organization</c>.</summary>
public sealed record SparkOrganization(string Name, string? Url = null)
{
    internal JsonObject ToJsonLd()
    {
        var organization = new JsonObject { ["@type"] = "Organization", ["name"] = Name };
        SparkStructuredData.Set(organization, "url", Url);
        return organization;
    }
}

/// <summary>
/// schema.org <c>Event</c> (commerce §4.14: offers, location, start in the venue's offset, status). Pass
/// <see cref="Start"/> already in the venue's offset (the core date-time facility, K10); it is written with that offset.
/// </summary>
public sealed record SparkEventData(
    string Name,
    DateTimeOffset Start,
    SparkPlace? Location = null,
    string? Description = null,
    DateTimeOffset? End = null,
    SparkEventStatus Status = SparkEventStatus.Scheduled,
    SparkEventAttendanceMode AttendanceMode = SparkEventAttendanceMode.Offline,
    string? OnlineUrl = null,
    IReadOnlyList<string>? Images = null,
    IReadOnlyList<SparkOffer>? Offers = null,
    SparkOrganization? Organizer = null,
    IReadOnlyList<string>? Performers = null,
    DateTimeOffset? PreviousStart = null) : SparkStructuredData
{
    public override JsonObject ToJsonLd()
    {
        var json = new JsonObject
        {
            ["@context"] = Context,
            ["@type"] = "Event",
            ["name"] = Name,
            ["startDate"] = IsoDateTime(Start),
            ["eventStatus"] = Context + "/Event" + Status,
            ["eventAttendanceMode"] = Context + "/" + AttendanceMode + "EventAttendanceMode",
        };
        Set(json, "description", Description);
        if (End is { } end)
            json["endDate"] = IsoDateTime(end);
        if (Status == SparkEventStatus.Rescheduled && PreviousStart is { } previous)
            json["previousStartDate"] = IsoDateTime(previous);

        var locations = new JsonArray();
        if (Location is not null && AttendanceMode != SparkEventAttendanceMode.Online)
            locations.Add(Location.ToJsonLd());
        if (OnlineUrl is not null && AttendanceMode != SparkEventAttendanceMode.Offline)
            locations.Add(new JsonObject { ["@type"] = "VirtualLocation", ["url"] = OnlineUrl });
        if (locations.Count == 1)
            json["location"] = locations[0]!.DeepClone();
        else if (locations.Count > 1)
            json["location"] = locations;

        SetArray(json, "image", Images);
        if (Offers is { Count: > 0 })
            json["offers"] = new JsonArray(Offers.Select(o => (JsonNode)o.ToJsonLd()).ToArray());
        if (Organizer is not null)
            json["organizer"] = Organizer.ToJsonLd();
        if (Performers is { Count: > 0 })
            json["performer"] = new JsonArray(Performers.Select(p => (JsonNode)new JsonObject { ["@type"] = "PerformingGroup", ["name"] = p }).ToArray());
        return json;
    }
}

/// <summary>schema.org <c>Product</c> (commerce §4.14: offers, GTIN, availability).</summary>
/// <param name="Gtin">A GTIN-8, -12, -13 or -14 (digits only), written as <c>gtin</c>.</param>
public sealed record SparkProductData(
    string Name,
    string? Description = null,
    IReadOnlyList<string>? Images = null,
    string? Sku = null,
    string? Gtin = null,
    string? Mpn = null,
    string? Brand = null,
    IReadOnlyList<SparkOffer>? Offers = null) : SparkStructuredData
{
    public override JsonObject ToJsonLd()
    {
        if (Gtin is not null && !(Gtin.Length is 8 or 12 or 13 or 14 && Gtin.All(char.IsAsciiDigit)))
            throw new ArgumentException($"'{Gtin}' is not a GTIN-8, -12, -13 or -14.", nameof(Gtin));

        var json = new JsonObject { ["@context"] = Context, ["@type"] = "Product", ["name"] = Name };
        Set(json, "description", Description);
        SetArray(json, "image", Images);
        Set(json, "sku", Sku);
        Set(json, "gtin", Gtin);
        Set(json, "mpn", Mpn);
        if (Brand is not null)
            json["brand"] = new JsonObject { ["@type"] = "Brand", ["name"] = Brand };
        if (Offers is { Count: > 0 })
            json["offers"] = new JsonArray(Offers.Select(o => (JsonNode)o.ToJsonLd()).ToArray());
        return json;
    }
}
