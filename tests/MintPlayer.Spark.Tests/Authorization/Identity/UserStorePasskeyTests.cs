using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Operations.CompareExchange;
using System.Security.Cryptography;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// The passkey half of <see cref="UserStore{TUser}"/>. The reservation lifecycle gets most of the
/// attention here, because every way of getting it wrong is silent: a reservation left behind burns
/// a credential id permanently, and one never taken lets two users claim the same credential.
/// </summary>
public class UserStorePasskeyTests : SparkTestDriver
{
    private static byte[] CredentialId(byte seed) => [seed, 2, 3, 4, 5, 6, 7, 8];

    private static UserPasskeyInfo PasskeyInfo(byte[] credentialId, uint signCount = 1, string? name = "Laptop")
        => new(credentialId, [9, 9, 9], DateTimeOffset.UtcNow, signCount, ["internal"], true, true, false, [1, 1], [2, 2])
        {
            Name = name,
        };

    private static string ReservationKey(byte[] credentialId)
        => "passkeys/" + Convert.ToBase64String(SHA256.HashData(credentialId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string?> ReservationValueAsync(byte[] credentialId)
    {
        var result = await Store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(ReservationKey(credentialId)));
        return result?.Value;
    }

    private async Task<SparkUser> CreateUserAsync(UserStore<SparkUser> store, string email)
    {
        var user = new SparkUser { UserName = email, Email = email, NormalizedEmail = email.ToUpperInvariant() };
        var result = await store.CreateAsync(user, CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        return user;
    }

    [Fact]
    public async Task Adding_a_passkey_reserves_the_credential_id()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "reserve@example.com");
        var credentialId = CredentialId(1);

        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId), CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        (await ReservationValueAsync(credentialId)).Should().Be(user.Id);
    }

    [Fact]
    public async Task FindByPasskeyId_resolves_the_owner()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "resolve@example.com");
        var credentialId = CredentialId(2);

        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId), CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        var found = await store.FindByPasskeyIdAsync(credentialId, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task FindByPasskeyId_returns_null_for_an_unknown_credential()
    {
        using var store = new UserStore<SparkUser>(Store);

        (await store.FindByPasskeyIdAsync(CredentialId(200), CancellationToken.None)).Should().BeNull();
    }

    /// <summary>AC10 — and the refusal must not say who holds it.</summary>
    [Fact]
    public async Task A_credential_id_cannot_be_claimed_by_two_users()
    {
        using var store = new UserStore<SparkUser>(Store);
        var first = await CreateUserAsync(store, "first@example.com");
        var second = await CreateUserAsync(store, "second@example.com");
        var credentialId = CredentialId(3);

        await store.AddOrUpdatePasskeyAsync(first, PasskeyInfo(credentialId), CancellationToken.None);
        await store.UpdateAsync(first, CancellationToken.None);

        var act = async () => await store.AddOrUpdatePasskeyAsync(second, PasskeyInfo(credentialId), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should()
            .NotContain(first.Id!, "naming the holder would make enrollment a lookup oracle")
            .And.NotContain("first@example.com");
    }

    /// <summary>
    /// The update path must not re-reserve, and must not overwrite the write-once fields. The sign
    /// count is the reason this method is called at all after enrollment.
    /// </summary>
    [Fact]
    public async Task Updating_a_passkey_advances_the_mutable_fields_only()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "update@example.com");
        var credentialId = CredentialId(4);

        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId, signCount: 1), CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);
        var createdAt = user.Passkeys.Single().CreatedAt;

        var advanced = new UserPasskeyInfo(credentialId, [7, 7, 7], DateTimeOffset.UtcNow.AddDays(1), 42, ["usb"], false, false, true, [3, 3], [4, 4])
        {
            Name = "Renamed",
        };
        await store.AddOrUpdatePasskeyAsync(user, advanced, CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        var stored = user.Passkeys.Should().ContainSingle().Which;
        stored.SignCount.Should().Be(42);
        stored.IsBackedUp.Should().BeTrue();
        stored.IsUserVerified.Should().BeFalse();
        stored.Name.Should().Be("Renamed");

        stored.PublicKey.Should().Equal(new byte[] { 9, 9, 9 }, "the public key is write-once");
        stored.CreatedAt.Should().Be(createdAt, "the creation time is write-once");
    }

    [Fact]
    public async Task Removing_a_passkey_releases_the_reservation()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "remove@example.com");
        var credentialId = CredentialId(5);

        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId), CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        await store.RemovePasskeyAsync(user, credentialId, CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        user.Passkeys.Should().BeEmpty();
        (await ReservationValueAsync(credentialId)).Should().BeNullOrEmpty("the credential id must become registerable again");
    }

    /// <summary>AC9 — the failure mode here is silent and permanent, so it gets its own test.</summary>
    [Fact]
    public async Task Deleting_a_user_releases_every_passkey_reservation()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "deleted@example.com");
        byte[] first = CredentialId(6), second = CredentialId(7);

        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(first), CancellationToken.None);
        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(second), CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        await store.DeleteAsync(user, CancellationToken.None);

        (await ReservationValueAsync(first)).Should().BeNullOrEmpty();
        (await ReservationValueAsync(second)).Should().BeNullOrEmpty();
    }

    /// <summary>
    /// A reservation that already points at this user is not a conflict. Without this, a retry after
    /// the document write failed would be locked out by its own half-finished attempt.
    /// </summary>
    [Fact]
    public async Task Re_enrolling_after_a_failed_document_write_succeeds()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "retry@example.com");
        var credentialId = CredentialId(8);

        // Simulates the reservation landing while the document write did not.
        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId), CancellationToken.None);
        user.Passkeys.Clear();

        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId), CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        user.Passkeys.Should().ContainSingle();
        (await ReservationValueAsync(credentialId)).Should().Be(user.Id);
    }

    [Fact]
    public async Task Get_and_find_round_trip_every_field()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "roundtrip@example.com");
        var credentialId = CredentialId(9);

        await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId, signCount: 5, name: "Phone"), CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);

        var all = await store.GetPasskeysAsync(user, CancellationToken.None);
        var single = all.Should().ContainSingle().Which;
        single.CredentialId.Should().Equal(credentialId);
        single.PublicKey.Should().Equal(new byte[] { 9, 9, 9 });
        single.SignCount.Should().Be(5u);
        single.Name.Should().Be("Phone");
        single.Transports.Should().BeEquivalentTo(["internal"]);

        var found = await store.FindPasskeyAsync(user, credentialId, CancellationToken.None);
        found.Should().NotBeNull();
        found!.Name.Should().Be("Phone");

        (await store.FindPasskeyAsync(user, CredentialId(201), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Passkeys_survive_a_reload_of_the_user_document()
    {
        var credentialId = CredentialId(10);
        string userId;

        using (var store = new UserStore<SparkUser>(Store))
        {
            var user = await CreateUserAsync(store, "persisted@example.com");
            await store.AddOrUpdatePasskeyAsync(user, PasskeyInfo(credentialId, signCount: 3), CancellationToken.None);
            await store.UpdateAsync(user, CancellationToken.None);
            userId = user.Id!;
        }

        using var reader = new UserStore<SparkUser>(Store);
        var reloaded = await reader.FindByIdAsync(userId, CancellationToken.None);

        reloaded.Should().NotBeNull();
        var stored = reloaded!.Passkeys.Should().ContainSingle().Which;
        stored.CredentialId.Should().Equal(credentialId, "byte arrays must survive the base64 round trip");
        stored.SignCount.Should().Be(3u);
    }

    [Fact]
    public async Task Removing_an_unknown_passkey_is_a_no_op()
    {
        using var store = new UserStore<SparkUser>(Store);
        var user = await CreateUserAsync(store, "noop@example.com");

        await store.RemovePasskeyAsync(user, CredentialId(202), CancellationToken.None);

        user.Passkeys.Should().BeEmpty();
    }
}
