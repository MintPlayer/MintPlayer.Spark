namespace Festival.Seed;

/// <summary>
/// The fixed document ids of the demo seed (<see cref="FestivalSeeder"/>). Deterministic, so a second start writes
/// nothing, and the E2E suite (<c>tests/MintPlayer.Spark.E2E.Tests/Festival</c>) names the same documents; it mirrors
/// these constants in <c>FestivalTestHost</c> because it does not reference the app.
/// </summary>
public static class FestivalIds
{
    /// <summary>The seed's version marker: a changed seed bumps it and seeds the missing documents.</summary>
    public const string Marker = "FestivalSeeds/v1";

    public const string Organization = "Organizations/festival-demo";

    public const string Venue = "Venues/festival-grounds";

    // ---------- the festival: three days, general admission ----------

    public const string Event = "TicketEvents/spark-summer-festival";
    public const string EventSlug = "spark-summer-festival";
    public const string Friday = "TicketEventDates/spark-summer-festival-fri";
    public const string Saturday = "TicketEventDates/spark-summer-festival-sat";
    public const string Sunday = "TicketEventDates/spark-summer-festival-sun";

    public const string DayTicket = "TicketTypes/spark-summer-festival-day";
    public const string WeekendPass = "TicketTypes/spark-summer-festival-weekend";
    public const string VipUpgrade = "TicketTypes/spark-summer-festival-vip";

    /// <summary>The open phase: no queue, on sale since the seed.</summary>
    public const string RegularPhase = "TicketSalePhases/spark-summer-festival-regular";

    public const string MainGateList = "TicketCheckInLists/spark-summer-festival-gate";
    public const string VipZoneList = "TicketCheckInLists/spark-summer-festival-vip";
    public const string Settings = "TicketEventSettings/spark-summer-festival";

    // ---------- the night session: a queued sale (waiting room) ----------

    public const string NightEvent = "TicketEvents/spark-night-session";
    public const string NightEventSlug = "spark-night-session";
    public const string NightDate = "TicketEventDates/spark-night-session";
    public const string NightTicket = "TicketTypes/spark-night-session-standing";
    public const string NightPhase = "TicketSalePhases/spark-night-session-presale";

    // ---------- the shop ----------

    public const string MerchandiseCategory = "ShopCategories/merchandise";
    public const string ClothingCategory = "ShopCategories/clothing";

    public const string TShirt = "ShopProducts/festival-t-shirt";
    public const string TShirtSlug = "festival-t-shirt";
    public const string TShirtS = "ShopVariants/festival-t-shirt-s";
    public const string TShirtM = "ShopVariants/festival-t-shirt-m";
    public const string TShirtL = "ShopVariants/festival-t-shirt-l";

    /// <summary>A reusable cup sold at a reduction, with its 30-day prior price in the history (GA3).</summary>
    public const string Cup = "ShopProducts/festival-cup";
    public const string CupSlug = "festival-cup";
    public const string CupVariant = "ShopVariants/festival-cup";

    /// <summary>A poster with one unit: two buyers race for it (the race-loser refund of PRD §14).</summary>
    public const string Poster = "ShopProducts/festival-poster";
    public const string PosterSlug = "festival-poster";
    public const string PosterVariant = "ShopVariants/festival-poster";

    // ---------- legal documents (platform-wide, GA2, GA10) ----------

    public const string TermsOfSale = "LegalDocuments/festival-terms-of-sale-v1";
    public const string PrivacyPolicy = "LegalDocuments/festival-privacy-policy-v1";
    public const string WithdrawalInformation = "LegalDocuments/festival-withdrawal-information-v1";
    public const string CookiePolicy = "LegalDocuments/festival-cookie-policy-v1";
    public const string AccessibilityStatement = "LegalDocuments/festival-accessibility-statement-v1";
}
