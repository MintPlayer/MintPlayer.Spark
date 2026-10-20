using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Delivery;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shop.Account;

/// <summary>
/// The signed-in buyer's address book (GB10, §4.14): one document per account, <c>ShopAddressBooks/{accountId}</c>, so
/// reading it is a point load and nobody else's book can be addressed. Entries are the delivery step's
/// <see cref="SparkShopAddress"/>, so a saved address fills that step as it is; they are validated by the same
/// <see cref="ShippingAddressRules"/> the step uses.
/// </summary>
/// <remarks>
/// PRD §4.14 names the reference-data <c>Address</c> (§4.18). Shop does not reference reference data, and the delivery
/// step, Shipping and the carriers all take <see cref="SparkShopAddress"/>; storing that shape keeps one address type
/// on the buying path. Erased with the account's personal data like every per-account document.
/// </remarks>
public sealed class ShopAddressBook
{
    public string? Id { get; set; }

    public string AccountId { get; set; } = "";

    public List<ShopAddressBookEntry> Entries { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The document id of <paramref name="accountId"/>'s book.</summary>
    public static string IdFor(string accountId) => "ShopAddressBooks/" + accountId;
}

/// <summary>One saved address.</summary>
public sealed class ShopAddressBookEntry
{
    public string EntryId { get; set; } = "";

    /// <summary>The buyer's own label ("Home", "Office"); optional.</summary>
    public string? Label { get; set; }

    public SparkShopAddress Address { get; set; } = new("", null, "", null, null, "", "", "", null, null);

    /// <summary>The address the delivery step proposes first.</summary>
    public bool IsDefault { get; set; }
}

internal sealed class ShopAddressBooks(IDocumentStore store, ISparkCurrentUser currentUser, TimeProvider clock)
{
    /// <summary>The most addresses one book keeps.</summary>
    internal const int MaxEntries = 20;

    private string? AccountId => currentUser is { IsAuthenticated: true, Id: { Length: > 0 } id } ? id : null;

    /// <summary>The caller's book; null when not signed in.</summary>
    public async Task<SparkShopAddressBook?> GetAsync(CancellationToken cancellationToken)
    {
        if (AccountId is not { } accountId)
            return null;
        using var session = store.OpenAsyncSession();
        var book = await session.LoadAsync<ShopAddressBook>(ShopAddressBook.IdFor(accountId), cancellationToken);
        return View(book);
    }

    /// <summary>Adds (no <see cref="SparkShopAddressSaveRequest.EntryId"/>) or replaces an entry.</summary>
    /// <exception cref="ShopException"><see cref="ShopRefusals.InvalidAddress"/>, <see cref="ShopRefusals.AddressBookFull"/>.</exception>
    public async Task<SparkShopAddressBook?> SaveAsync(SparkShopAddressSaveRequest request, CancellationToken cancellationToken)
    {
        if (AccountId is not { } accountId)
            return null;
        ArgumentNullException.ThrowIfNull(request);
        var address = Clean(request.Address ?? throw ShopException.Of(ShopRefusals.InvalidAddress));

        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var id = ShopAddressBook.IdFor(accountId);
        var book = await session.LoadAsync<ShopAddressBook>(id, cancellationToken);
        if (book is null)
        {
            book = new ShopAddressBook { Id = id, AccountId = accountId };
            await session.StoreAsync(book, id, cancellationToken);
        }
        Apply(book, request.EntryId, request.Label, address, request.IsDefault);
        book.UpdatedAt = clock.GetUtcNow();
        await session.SaveChangesAsync(cancellationToken);
        return View(book);
    }

    /// <summary>Removes an entry; removing the default makes the first remaining one the default.</summary>
    public async Task<SparkShopAddressBook?> DeleteAsync(string entryId, CancellationToken cancellationToken)
    {
        if (AccountId is not { } accountId)
            return null;
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var book = await session.LoadAsync<ShopAddressBook>(ShopAddressBook.IdFor(accountId), cancellationToken);
        if (book is null)
            return View(null);
        if (Remove(book, entryId))
        {
            book.UpdatedAt = clock.GetUtcNow();
            await session.SaveChangesAsync(cancellationToken);
        }
        return View(book);
    }

    /// <summary>Trims and validates an address with the delivery step's own rules.</summary>
    internal static SparkShopAddress Clean(SparkShopAddress address)
    {
        var normalized = ShippingAddressRules.Normalize(DeliveryMath.Address(address));
        if (ShippingAddressRules.Validate(normalized, "address").Count > 0 || string.IsNullOrWhiteSpace(normalized.PostalCode))
            throw ShopException.Of(ShopRefusals.InvalidAddress);
        return DeliveryMath.View(normalized);
    }

    /// <summary>The pure edit, separated for the unit test.</summary>
    internal static void Apply(ShopAddressBook book, string? entryId, string? label, SparkShopAddress address, bool isDefault)
    {
        var entry = entryId is { Length: > 0 } ? book.Entries.FirstOrDefault(e => e.EntryId == entryId) : null;
        if (entry is null)
        {
            if (book.Entries.Count >= MaxEntries)
                throw ShopException.Of(ShopRefusals.AddressBookFull);
            entry = new ShopAddressBookEntry { EntryId = Guid.NewGuid().ToString("N") };
            book.Entries.Add(entry);
        }
        entry.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 60)];
        entry.Address = address;
        if (isDefault || book.Entries.Count == 1)
        {
            foreach (var other in book.Entries)
                other.IsDefault = other == entry;
        }
        else if (!book.Entries.Any(e => e.IsDefault))
        {
            book.Entries[0].IsDefault = true;
        }
    }

    /// <summary>The pure removal, separated for the unit test; false when there was no such entry.</summary>
    internal static bool Remove(ShopAddressBook book, string entryId)
    {
        var removed = book.Entries.RemoveAll(e => e.EntryId == entryId) > 0;
        if (removed && book.Entries.Count > 0 && !book.Entries.Any(e => e.IsDefault))
            book.Entries[0].IsDefault = true;
        return removed;
    }

    internal static SparkShopAddressBook View(ShopAddressBook? book)
        => new((book?.Entries ?? []).Select(e => new SparkShopAddressBookEntry(e.EntryId, e.Label, e.Address, e.IsDefault)).ToList());
}

/// <summary>GDPR (Art. 15, 20): the address book in the personal-data export.</summary>
internal sealed class ShopAddressBookPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    public string Name => "shopAddressBook";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;
        using var session = store.OpenAsyncSession();
        var book = await session.LoadAsync<ShopAddressBook>(ShopAddressBook.IdFor(userId), cancellationToken);
        return book is null || book.Entries.Count == 0 ? null : ShopAddressBooks.View(book).Entries;
    }
}

/// <summary>GDPR (Art. 17): the address book goes with the account.</summary>
internal sealed class ShopAddressBookAccountDeletion<TUser>(IDocumentStore store) : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;
        using var session = store.OpenAsyncSession();
        session.Delete(ShopAddressBook.IdFor(userId));
        await session.SaveChangesAsync(cancellationToken);
    }
}
