using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.WaitingRoom;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.WaitingRoom;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Festival.Seed;

/// <summary>
/// The demo's deterministic seed (commerce #521 M18, PRD §4.16): one seller organization in <c>ConnectedSellers</c> mode
/// (approved, a verified trader, a verified test-provider account), a three-day general-admission festival with day
/// tickets, a weekend pass and a VIP add-on, a queued night session, a shop with a T-shirt, a reduced cup and a
/// one-off poster, and the platform's legal documents.
/// </summary>
/// <remarks>
/// <para>
/// <b>No personal data.</b> Every name, address and e-mail is fictitious and organisational (<c>*.example</c>
/// domains, a made-up street on the reference-data basic seed's localities: BE, NL, FR, LU, DE). No person is named.
/// <c>FestivalSeedTests</c> guards it.
/// </para>
/// <para>
/// <b>Deterministic.</b> Fixed ids (<see cref="FestivalIds"/>); dates derive from the first start's day (the festival
/// weekend is the first Friday of July at least 60 days ahead), and a second start writes nothing (the
/// <see cref="FestivalIds.Marker"/> document). Documents are written with a raw session, outside the
/// persistent-object pipeline, so the seeder does what the publishers would: quotas, <c>ProvisionedAt</c>,
/// <c>PublishedAt</c>, the shop's maintained index fields and the phases' waiting rooms.
/// </para>
/// </remarks>
internal sealed class FestivalSeeder(IServiceProvider services, IConfiguration configuration, TimeProvider clock, ILogger<FestivalSeeder> logger) : IHostedLifecycleService
{
    public const string EnabledKey = "Festival:Seed:Enabled";

    /// <summary>One seller: the demo organisation (fictitious).</summary>
    public const string OrganizationName = "Spark Festival Demo Organisation";

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(EnabledKey, true))
            return;
        try
        {
            await SeedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The festival demo seed failed.");
            throw;
        }
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        using (var check = store.OpenAsyncSession())
        {
            if (await check.Advanced.ExistsAsync(FestivalIds.Marker, cancellationToken))
                return;
        }

        var now = clock.GetUtcNow();
        var plan = FestivalSeedPlan.Create(now);

        using (var session = store.OpenAsyncSession())
        {
            foreach (var document in plan.Documents)
                await session.StoreAsync(document.Entity, document.Id, cancellationToken);

            await session.StoreAsync(new FestivalSeedMarker { SeededAt = now, FestivalStartsAt = plan.FestivalFriday }, FestivalIds.Marker, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        // Quotas (TicketEventPublisher.ProvisionDateAsync, ShopPublisher): idempotent creates.
        var inventory = scope.ServiceProvider.GetRequiredService<ISparkInventory>();
        foreach (var (quotaId, capacity, shards) in plan.Quotas)
            await inventory.CreateQuotaAsync(quotaId, capacity, shards, cancellationToken);

        // The queued phase's waiting room (the after-save interceptors do this for pipeline writes).
        var rooms = scope.ServiceProvider.GetRequiredService<ISparkCommerceWaitingRooms>();
        await rooms.SyncEventAsync(FestivalIds.NightEvent, cancellationToken);
        await rooms.SyncEventAsync(FestivalIds.Event, cancellationToken);

        logger.LogInformation("Festival demo seeded: festival weekend starts {Friday:yyyy-MM-dd}.", plan.FestivalFriday);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The seed's version marker (<see cref="FestivalIds.Marker"/>).</summary>
public sealed class FestivalSeedMarker
{
    public DateTimeOffset SeededAt { get; set; }

    public DateTimeOffset FestivalStartsAt { get; set; }
}

/// <summary>A document to store under a fixed id.</summary>
public sealed record SeedDocument(string Id, object Entity);

/// <summary>
/// The seed as data, pure (no database): every document with its id, and the quotas to create. Pure so a unit test can
/// check its determinism and its "no personal data" rule (<c>FestivalSeedTests</c>).
/// </summary>
public sealed class FestivalSeedPlan
{
    public const string Currency = "EUR";
    public const string Zone = "Europe/Brussels";

    /// <summary>Every fictitious e-mail address in the seed ends in this domain.</summary>
    public const string MailDomain = "festival.example";

    public required DateTimeOffset FestivalFriday { get; init; }

    public required IReadOnlyList<SeedDocument> Documents { get; init; }

    public required IReadOnlyList<(string QuotaId, int Capacity, int Shards)> Quotas { get; init; }

    public IEnumerable<ShopProduct> Products => Documents.Select(d => d.Entity).OfType<ShopProduct>();

    public IReadOnlyList<ShopVariant> VariantsOf(string productId)
        => Documents.Select(d => d.Entity).OfType<ShopVariant>().Where(v => v.ProductId == productId).ToList();

    /// <summary>
    /// The first Friday of July at least 60 days after <paramref name="now"/>, 14:00 Brussels summer time (UTC+2; the
    /// whole of July is CEST).
    /// </summary>
    public static DateTimeOffset FestivalFridayFor(DateTimeOffset now)
    {
        var earliest = now.UtcDateTime.Date.AddDays(60);
        var year = earliest.Month > 7 || (earliest.Month == 7 && earliest.Day > 7) ? earliest.Year + 1 : earliest.Year;
        var day = new DateTime(year, 7, 1);
        while (day.DayOfWeek != DayOfWeek.Friday)
            day = day.AddDays(1);
        if (day < earliest)
        {
            day = new DateTime(year + 1, 7, 1);
            while (day.DayOfWeek != DayOfWeek.Friday)
                day = day.AddDays(1);
        }
        return new DateTimeOffset(day.Year, day.Month, day.Day, 14, 0, 0, TimeSpan.FromHours(2));
    }

    public static FestivalSeedPlan Create(DateTimeOffset now)
    {
        var org = FestivalIds.Organization;
        var friday = FestivalFridayFor(now);
        var docs = new List<SeedDocument>();
        var quotas = new List<(string, int, int)>();
        void Add(string id, object entity) => docs.Add(new SeedDocument(id, entity));

        // ---------- the seller (D5, D21, GA5, GA6) ----------
        Add(org, new Organization { Id = org, Name = FestivalSeeder.OrganizationName, CreatedAtUtc = now.UtcDateTime });
        Add(SellerProfile.IdFor(org), new SellerProfile
        {
            Id = SellerProfile.IdFor(org),
            OrganizationId = org,
            Status = SellerRegistrationStatus.Approved,
            CreatedAt = now,
            SubmittedAt = now,
            ApprovedAt = now,
            TraderStatus = TraderStatus.Trader,
            Trader = new TraderData
            {
                Name = FestivalSeeder.OrganizationName,
                Address = "Festivalweide 1, 9000 Gent, BE",
                Country = "BE",
                Phone = "+32 9 000 00 00",
                Email = "organiser@" + MailDomain,
                HasIdentityDocument = true,
                PaymentAccount = "BE00 0000 0000 0000",
                TradeRegister = "Demo register",
                RegistrationNumber = "0000.000.000",
                SelfCertification = true,
                SelfCertifiedAt = now,
            },
            Verification = new TraderVerification { Method = "manual", Passed = true, By = "festival-seed", At = now, Notes = "Demo seed." },
            Kyc = KycStatus.Verified,
            Dac7 = new()
            {
                IsEntity = true,
                Tin = "0000000000",
                TinCountry = "BE",
                PrimaryAddress = "Festivalweide 1, 9000 Gent, BE",
                BusinessRegistrationNumber = "0000.000.000",
                Iban = "BE00 0000 0000 0000",
                AccountHolder = FestivalSeeder.OrganizationName,
            },
        });
        var account = SellerPaymentAccount.IdFor("test", org);
        Add(account, new SellerPaymentAccount
        {
            Id = account,
            SellerOrganizationId = org,
            Provider = "test",
            AccountId = "tacct_festival_demo",
            AccountName = FestivalSeeder.OrganizationName,
            Kyc = KycStatus.Verified,
            ChargesEnabled = true,
            PayoutsEnabled = true,
            UpdatedAtUtc = now.UtcDateTime,
        });

        // ---------- the venue (general admission: no layout) ----------
        var address = new Address
        {
            CountryCode = "BE",
            StreetName = "Festivalweide",
            HouseNumber = "1",
            PostalCode = "9000",
            LocalityName = "Gent",
            TimeZone = Zone,
            Lines = ["Festivalweide 1", "9000 Gent", "Belgium"],
        };
        Add(FestivalIds.Venue, new Venue
        {
            Id = FestivalIds.Venue,
            Name = "Festival grounds",
            Description = "An open-air field with three stages (fictitious).",
            Address = address,
            TimeZone = Zone,
            CreatedBy = "festival-seed",
            CreatedAtUtc = now.UtcDateTime,
        });

        // ---------- the festival ----------
        var festival = new TicketEvent
        {
            Id = FestivalIds.Event,
            OrganizationId = org,
            IsPublished = true,
            Name = TranslatedString.Of(("en", "Spark Summer Festival"), ("nl", "Spark Zomerfestival"), ("fr", "Festival d'été Spark")),
            Slug = FestivalIds.EventSlug,
            Description = TranslatedString.Of(("en", "Three days of music in the open air. A demo event: nothing here is real."), ("nl", "Drie dagen muziek in open lucht. Een demo: niets hiervan is echt.")),
            Channel = "tickets",
            Status = ETicketEventStatus.Scheduled,
            Performers = ["The Example Band", "Placeholder Quartet", "Demo DJ"],
            Policy = new TicketSalePolicy { PerBuyerLimit = 8, Transfer = true, FaceValueResale = true },
            PublishedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Add(festival.Id!, festival);
        var days = new[] { (FestivalIds.Friday, "Friday", 0), (FestivalIds.Saturday, "Saturday", 1), (FestivalIds.Sunday, "Sunday", 2) };
        foreach (var (id, name, offset) in days)
        {
            var starts = friday.AddDays(offset);
            Add(id, new TicketEventDate
            {
                Id = id,
                OrganizationId = org,
                EventId = festival.Id!,
                Name = TranslatedString.Of("en", name),
                StartsAt = starts,
                DoorsOpenAt = starts.AddHours(-1),
                EndsAt = starts.AddHours(10),
                VenueId = FestivalIds.Venue,
                VenueTimeZoneId = Zone,
                Capacity = 500,
                SortOrder = offset,
                ProvisionedAt = now,
            });
            quotas.Add((TicketQuotas.Date(id), 500, TicketQuotas.Shards(500, 16)));
        }
        Add(FestivalIds.DayTicket, new TicketType
        {
            Id = FestivalIds.DayTicket, OrganizationId = org, EventId = festival.Id!, SortOrder = 0,
            Name = TranslatedString.Of(("en", "Day ticket"), ("nl", "Dagticket")), Kind = ETicketTypeKind.Admission, VatRateBasisPoints = 600,
        });
        Add(FestivalIds.WeekendPass, new TicketType
        {
            Id = FestivalIds.WeekendPass, OrganizationId = org, EventId = festival.Id!, SortOrder = 1,
            Name = TranslatedString.Of(("en", "Weekend pass (Saturday and Sunday)"), ("nl", "Weekendpas (zaterdag en zondag)")),
            Kind = ETicketTypeKind.Pass, DateIds = [FestivalIds.Saturday, FestivalIds.Sunday], VatRateBasisPoints = 600,
        });
        Add(FestivalIds.VipUpgrade, new TicketType
        {
            Id = FestivalIds.VipUpgrade, OrganizationId = org, EventId = festival.Id!, SortOrder = 2,
            Name = TranslatedString.Of(("en", "VIP area upgrade"), ("nl", "VIP-zone upgrade")),
            Kind = ETicketTypeKind.AddOn, ParentTypeIds = [FestivalIds.DayTicket], CapacityPerDate = 50, VatRateBasisPoints = 2100,
        });
        foreach (var (dateId, _, _) in days)
            quotas.Add((TicketQuotas.TypeOnDate(dateId, FestivalIds.VipUpgrade), 50, TicketQuotas.Shards(50, 16)));
        Add(FestivalIds.RegularPhase, new TicketSalePhase
        {
            Id = FestivalIds.RegularPhase, OrganizationId = org, EventId = festival.Id!,
            Name = TranslatedString.Of(("en", "Regular sale"), ("nl", "Gewone verkoop")),
            StartsAt = now.AddMinutes(-1),
            EndsAt = friday.AddDays(2),
            Prices =
            [
                new TicketPhasePrice { TicketTypeId = FestivalIds.DayTicket, Price = new Money(49m, Currency) },
                new TicketPhasePrice { TicketTypeId = FestivalIds.WeekendPass, Price = new Money(89m, Currency) },
                new TicketPhasePrice { TicketTypeId = FestivalIds.VipUpgrade, Price = new Money(25m, Currency) },
            ],
        });
        Add(FestivalIds.Settings, new TicketEventSettings
        {
            Id = FestivalIds.Settings, OrganizationId = org, EventId = festival.Id!,
            DeliveryLeadTimeHours = 0, RevokeOnDisputeBeforeDecision = false, UpdatedAt = now,
        });
        // GB3: the main gate admits every ticket and allows re-entry; the VIP zone admits the VIP upgrade only.
        Add(FestivalIds.MainGateList, new TicketCheckInList
        {
            Id = FestivalIds.MainGateList, OrganizationId = org, EventId = festival.Id!, Name = "Main gate",
            Kind = ETicketCheckInListKind.Gate, AllowReentry = true, SortOrder = 0,
        });
        Add(FestivalIds.VipZoneList, new TicketCheckInList
        {
            Id = FestivalIds.VipZoneList, OrganizationId = org, EventId = festival.Id!, Name = "VIP zone",
            Kind = ETicketCheckInListKind.Zone, TicketTypeIds = [FestivalIds.VipUpgrade], AllowReentry = true, SortOrder = 1,
        });

        // ---------- the night session: a queued presale (waiting room, D10) ----------
        var night = new TicketEvent
        {
            Id = FestivalIds.NightEvent,
            OrganizationId = org,
            IsPublished = true,
            Name = TranslatedString.Of(("en", "Spark Night Session"), ("nl", "Spark Nachtsessie")),
            Slug = FestivalIds.NightEventSlug,
            Description = TranslatedString.Of("en", "A late-night show with a queued presale. A demo event."),
            Channel = "tickets",
            Policy = new TicketSalePolicy { PerBuyerLimit = 4, QueueMode = WaitingRoomMode.Fifo },
            PublishedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Add(night.Id!, night);
        var nightStarts = friday.AddHours(8);
        Add(FestivalIds.NightDate, new TicketEventDate
        {
            Id = FestivalIds.NightDate, OrganizationId = org, EventId = night.Id!, StartsAt = nightStarts,
            DoorsOpenAt = nightStarts.AddMinutes(-30), EndsAt = nightStarts.AddHours(4), VenueId = FestivalIds.Venue,
            VenueTimeZoneId = Zone, Capacity = 200, ProvisionedAt = now,
        });
        quotas.Add((TicketQuotas.Date(FestivalIds.NightDate), 200, TicketQuotas.Shards(200, 16)));
        Add(FestivalIds.NightTicket, new TicketType
        {
            Id = FestivalIds.NightTicket, OrganizationId = org, EventId = night.Id!,
            Name = TranslatedString.Of(("en", "Standing"), ("nl", "Staanplaats")), Kind = ETicketTypeKind.Admission, VatRateBasisPoints = 600,
        });
        Add(FestivalIds.NightPhase, new TicketSalePhase
        {
            Id = FestivalIds.NightPhase, OrganizationId = org, EventId = night.Id!,
            Name = TranslatedString.Of(("en", "Presale"), ("nl", "Voorverkoop")),
            QueueEnabled = true,
            QueueOpensAt = now.AddMinutes(-2),
            StartsAt = now.AddMinutes(-1),
            EndsAt = nightStarts,
            Prices = [new TicketPhasePrice { TicketTypeId = FestivalIds.NightTicket, Price = new Money(35m, Currency) }],
        });

        // ---------- the shop ----------
        Add(FestivalIds.MerchandiseCategory, new ShopCategory
        {
            Id = FestivalIds.MerchandiseCategory, Name = TranslatedString.Of(("en", "Merchandise"), ("nl", "Merchandise")), Slug = "merchandise", SortOrder = 0,
        });
        Add(FestivalIds.ClothingCategory, new ShopCategory
        {
            Id = FestivalIds.ClothingCategory, Name = TranslatedString.Of(("en", "Clothing"), ("nl", "Kleding")), Slug = "clothing",
            ParentId = FestivalIds.MerchandiseCategory, FacetKeys = ["size"], SortOrder = 1,
        });

        var tshirt = Goods(FestivalIds.TShirt, FestivalIds.TShirtSlug, ("Festival T-shirt", "Festival-T-shirt"), 25m, FestivalIds.ClothingCategory, now);
        tshirt.Attributes = [new ShopProductAttribute { Name = "material", Value = "Organic cotton" }];
        Add(tshirt.Id!, tshirt);
        var sizes = new[] { (FestivalIds.TShirtS, "S", "2000000000015"), (FestivalIds.TShirtM, "M", "2000000000022"), (FestivalIds.TShirtL, "L", "2000000000039") };
        var order = 0;
        foreach (var (id, size, gtin) in sizes)
        {
            Add(id, Variant(id, tshirt.Id!, "FEST-TEE-" + size, gtin, 100, 200, order++, now, new ShopVariantOption { Name = "size", Value = size }));
            quotas.Add((ShopQuotas.Variant(id), 100, ShopQuotas.Shards(100, 4)));
        }

        var cup = Goods(FestivalIds.Cup, FestivalIds.CupSlug, ("Reusable festival cup", "Herbruikbare festivalbeker"), 6m, FestivalIds.MerchandiseCategory, now);
        Add(cup.Id!, cup);
        Add(FestivalIds.CupVariant, Variant(FestivalIds.CupVariant, cup.Id!, "FEST-CUP", "2000000000046", 300, 80, 0, now));
        quotas.Add((ShopQuotas.Variant(FestivalIds.CupVariant), 300, ShopQuotas.Shards(300, 4)));

        var poster = Goods(FestivalIds.Poster, FestivalIds.PosterSlug, ("Signed line-up poster (one copy)", "Gesigneerde affiche (één exemplaar)"), 30m, FestivalIds.MerchandiseCategory, now);
        Add(poster.Id!, poster);
        Add(FestivalIds.PosterVariant, Variant(FestivalIds.PosterVariant, poster.Id!, "FEST-POSTER", "2000000000053", 1, 150, 0, now));
        quotas.Add((ShopQuotas.Variant(FestivalIds.PosterVariant), 1, 1));

        // GA3: the cup's price history: 6.00 since 40 days ago, then a reduction to 4.50 for the festival season. The
        // prior price shown with the reduction is the lowest of the 30 days before it: 6.00.
        var cupItem = ShopItemIds.For(FestivalIds.CupVariant);
        var since = now.AddDays(-40);
        Add(PriceChangeId(cupItem, since), new CommercePriceChange
        {
            ItemId = cupItem, OrganizationId = org, Channel = "shop", At = since,
            After = new Money(6m, Currency), UnitPrice = new Money(6m, Currency), Reason = "create", ChangedBy = "festival-seed",
        });
        var saleFrom = now.AddMinutes(-1);
        Add(PromotionSalePrice.IdFor(cupItem, saleFrom), new PromotionSalePrice
        {
            ItemId = cupItem, Channel = "shop", OrganizationId = org,
            SalePrice = new Money(4.5m, Currency), RegularPrice = new Money(6m, Currency), PriceBasis = PriceBasis.Gross,
            VatRateBasisPoints = 2100, ValidFrom = saleFrom, ValidUntil = friday.AddDays(3), SeriesStart = saleFrom,
            CreatedBy = "festival-seed", CreatedAt = now,
        });

        // ---------- legal documents: platform-wide (OrganizationId null), version 1 (GA2, GA9, GA10) ----------
        Legal(FestivalIds.TermsOfSale, LegalDocumentKinds.TermsOfSale, "Terms of sale", FestivalLegalTexts.Terms);
        Legal(FestivalIds.PrivacyPolicy, LegalDocumentKinds.PrivacyPolicy, "Privacy policy", FestivalLegalTexts.Privacy);
        Legal(FestivalIds.WithdrawalInformation, LegalDocumentKinds.WithdrawalInformation, "Right of withdrawal", FestivalLegalTexts.Withdrawal);
        Legal(FestivalIds.CookiePolicy, LegalDocumentKinds.CookiePolicy, "Cookies", FestivalLegalTexts.Cookies);
        Legal(FestivalIds.AccessibilityStatement, LegalDocumentKinds.AccessibilityStatement, "Accessibility statement", FestivalLegalTexts.Accessibility);

        var plan = new FestivalSeedPlan { FestivalFriday = friday, Documents = docs, Quotas = quotas };
        var categories = docs.Select(d => d.Entity).OfType<ShopCategory>().ToDictionary(c => c.Id!, StringComparer.Ordinal);
        foreach (var product in plan.Products)
            Refresh(product, plan.VariantsOf(product.Id!), categories);
        return plan;

        void Legal(string id, string kind, string title, string body) => Add(id, new LegalDocument
        {
            Id = id, Kind = kind, OrganizationId = null, Version = 1,
            Title = TranslatedString.Of("en", title), Body = TranslatedString.Of("en", body),
            EffectiveFrom = now.AddMinutes(-1), PublishedAt = now, PublishedBy = "festival-seed",
        });
    }

    /// <summary>
    /// The product's maintained fields the catalogue index reads, as Shop's publisher computes them on a pipeline save
    /// (<c>ShopPublisher.RefreshAsync</c>, internal): facets, search text, the price-from, and the category path.
    /// </summary>
    private static void Refresh(ShopProduct product, IReadOnlyList<ShopVariant> variants, IReadOnlyDictionary<string, ShopCategory> categories)
    {
        product.FacetValues = ProductRules.FacetValues(product, variants);
        product.SearchText = ProductRules.SearchText(product, variants);
        (product.PriceFromMinor, product.Currency) = ProductRules.PriceFrom(product, variants);
        product.PriceVaries = variants.Where(v => v.IsActive).Select(v => ProductRules.PriceOf(product, v)?.ToMinorUnits()).Distinct().Count() > 1;
        var path = new List<string>();
        foreach (var start in product.CategoryIds)
            for (var id = start; id is not null && !path.Contains(id); id = categories.TryGetValue(id, out var c) ? c.ParentId : null)
                path.Add(id);
        product.CategoryPath = path;
    }

    /// <summary>The price-history id <c>SparkPriceHistory</c> uses (<c>CommercePriceChanges/{itemId}/{ticks:D19}</c>).</summary>
    public static string PriceChangeId(string itemId, DateTimeOffset at) => $"CommercePriceChanges/{itemId}/{at.UtcTicks:D19}";

    private static ShopProduct Goods(string id, string slug, (string En, string Nl) name, decimal price, string categoryId, DateTimeOffset now) => new()
    {
        Id = id,
        OrganizationId = FestivalIds.Organization,
        IsPublished = true,
        Name = TranslatedString.Of(("en", name.En), ("nl", name.Nl)),
        Slug = slug,
        Description = TranslatedString.Of("en", "Festival merchandise. A demo product: nothing is shipped."),
        Brand = "Spark Festival",
        Channel = "shop",
        Kind = EShopProductKind.Goods,
        CategoryIds = [categoryId],
        Price = new Money(price, Currency),
        VatRateBasisPoints = 2100,
        // GPSR (Reg. (EU) 2023/988 Art. 19): an EU manufacturer, fictitious.
        ManufacturerName = FestivalSeeder.OrganizationName,
        ManufacturerAddress = "Festivalweide 1, 9000 Gent, Belgium",
        ManufacturerContact = "products@" + MailDomain,
        ManufacturerCountry = "BE",
        OriginCountry = "BE",
        Media = [new ShopProductMedia { Url = "/icons/icon-512x512.png", Alt = TranslatedString.Of("en", name.En) }],
        PublishedAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static ShopVariant Variant(string id, string productId, string sku, string gtin, int capacity, int weightGrams, int sortOrder, DateTimeOffset now, params ShopVariantOption[] options) => new()
    {
        Id = id,
        OrganizationId = FestivalIds.Organization,
        ProductId = productId,
        Sku = sku,
        Gtin = gtin,
        Options = [.. options],
        WeightGrams = weightGrams,
        TrackInventory = true,
        Capacity = capacity,
        IsActive = true,
        SortOrder = sortOrder,
        ProvisionedAt = now,
    };
}
