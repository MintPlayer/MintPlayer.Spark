using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Account;
using MintPlayer.Spark.Commerce.Shop.Contracts;

namespace MintPlayer.Spark.Commerce.Tests.Shop;

/// <summary>M17 / GB10: the account area's address book (pure edits; the endpoints only load and save the document).</summary>
public class ShopAddressBookTests
{
    private static SparkShopAddress Address(string street = "Kerkstraat")
        => new("A. Buyer", null, street, "12", null, "9770", "Kruisem", "BE", null, null);

    [Fact]
    public void The_first_entry_becomes_the_default_and_a_new_default_moves_the_mark()
    {
        var book = new ShopAddressBook { AccountId = "users/1" };
        ShopAddressBooks.Apply(book, null, "Home", Address(), isDefault: false);
        book.Entries.Should().HaveCount(1);
        book.Entries[0].IsDefault.Should().BeTrue();

        ShopAddressBooks.Apply(book, null, "Office", Address("Stationsstraat"), isDefault: true);
        book.Entries.Single(e => e.IsDefault).Label.Should().Be("Office");
        book.Entries.Count(e => e.IsDefault).Should().Be(1);
    }

    [Fact]
    public void Saving_an_existing_entry_replaces_it()
    {
        var book = new ShopAddressBook();
        ShopAddressBooks.Apply(book, null, "Home", Address(), isDefault: false);
        var id = book.Entries[0].EntryId;
        ShopAddressBooks.Apply(book, id, "  Home 2  ", Address("Dorpsstraat"), isDefault: false);
        book.Entries.Should().HaveCount(1);
        book.Entries[0].Label.Should().Be("Home 2");
        book.Entries[0].Address.Street.Should().Be("Dorpsstraat");
        book.Entries[0].IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Removing_the_default_promotes_the_next_entry()
    {
        var book = new ShopAddressBook();
        ShopAddressBooks.Apply(book, null, "Home", Address(), isDefault: true);
        ShopAddressBooks.Apply(book, null, "Office", Address("Stationsstraat"), isDefault: false);
        ShopAddressBooks.Remove(book, book.Entries[0].EntryId).Should().BeTrue();
        book.Entries.Should().HaveCount(1);
        book.Entries[0].IsDefault.Should().BeTrue();
        ShopAddressBooks.Remove(book, "missing").Should().BeFalse();
    }

    [Fact]
    public void A_full_book_refuses_a_new_entry()
    {
        var book = new ShopAddressBook();
        for (var i = 0; i < ShopAddressBooks.MaxEntries; i++)
            ShopAddressBooks.Apply(book, null, null, Address(), isDefault: false);
        var refused = false;
        try { ShopAddressBooks.Apply(book, null, null, Address(), isDefault: false); }
        catch (ShopException ex) when (ex.Code == ShopRefusals.AddressBookFull) { refused = true; }
        refused.Should().BeTrue();
    }

    [Fact]
    public void An_invalid_address_is_refused_with_the_delivery_step_rules()
    {
        var refused = false;
        try { ShopAddressBooks.Clean(new SparkShopAddress("", null, "", null, null, "", "", "XX1", null, null)); }
        catch (ShopException ex) when (ex.Code == ShopRefusals.InvalidAddress) { refused = true; }
        refused.Should().BeTrue();
    }

    [Fact]
    public void Each_book_has_its_own_document_id()
        => ShopAddressBook.IdFor("users/1").Should().Be("ShopAddressBooks/users/1");
}
