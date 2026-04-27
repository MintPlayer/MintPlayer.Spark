using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// Pins the persistence and identity-store contract for <see cref="RoleStore"/>. The store
/// derives role document IDs deterministically from the role name, so a regression in the
/// ID convention silently breaks <c>FindByName</c> across the entire authorization stack.
/// </summary>
public class RoleStoreTests : SparkTestDriver
{
    private RoleStore CreateStore() => new(Store);

    [Fact]
    public async Task CreateAsync_assigns_deterministic_id_derived_from_name()
    {
        using var store = CreateStore();
        var role = new SparkRole { Name = "Admins" };

        var result = await store.CreateAsync(role, CancellationToken.None);

        result.Should().Be(IdentityResult.Success);
        role.Id.Should().NotBeNullOrEmpty();
        role.Id.Should().EndWith("admins");
    }

    [Fact]
    public async Task CreateAsync_persists_role_visible_from_a_separate_store_instance()
    {
        var creating = CreateStore();
        var role = new SparkRole { Name = "Editors", NormalizedName = "EDITORS" };
        await creating.CreateAsync(role, CancellationToken.None);
        creating.Dispose();

        using var reading = CreateStore();
        var loaded = await reading.FindByIdAsync(role.Id!, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Editors");
        loaded.NormalizedName.Should().Be("EDITORS");
    }

    [Fact]
    public async Task FindByIdAsync_returns_null_when_missing()
    {
        using var store = CreateStore();

        var loaded = await store.FindByIdAsync("sparkroles/missing", CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task FindByNameAsync_uses_deterministic_id_and_is_case_insensitive()
    {
        var creating = CreateStore();
        await creating.CreateAsync(new SparkRole { Name = "Admins" }, CancellationToken.None);
        creating.Dispose();

        using var reading = CreateStore();
        var loaded = await reading.FindByNameAsync("ADMINS", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Admins");
    }

    [Fact]
    public async Task FindByNameAsync_returns_null_when_missing()
    {
        using var store = CreateStore();

        var loaded = await store.FindByNameAsync("Nope", CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_persists_mutations()
    {
        var role = new SparkRole { Name = "Editors", NormalizedName = "EDITORS" };
        var creating = CreateStore();
        await creating.CreateAsync(role, CancellationToken.None);
        creating.Dispose();

        var updating = CreateStore();
        var loaded = await updating.FindByIdAsync(role.Id!, CancellationToken.None);
        loaded!.NormalizedName = "EDITORS-V2";
        var result = await updating.UpdateAsync(loaded, CancellationToken.None);
        updating.Dispose();

        result.Should().Be(IdentityResult.Success);

        using var verifying = CreateStore();
        var verified = await verifying.FindByIdAsync(role.Id!, CancellationToken.None);
        verified!.NormalizedName.Should().Be("EDITORS-V2");
    }

    [Fact]
    public async Task DeleteAsync_removes_the_document()
    {
        var role = new SparkRole { Name = "Temps" };
        var creating = CreateStore();
        await creating.CreateAsync(role, CancellationToken.None);
        creating.Dispose();

        var deleting = CreateStore();
        var loaded = await deleting.FindByIdAsync(role.Id!, CancellationToken.None);
        var result = await deleting.DeleteAsync(loaded!, CancellationToken.None);
        deleting.Dispose();

        result.Should().Be(IdentityResult.Success);

        using var verifying = CreateStore();
        (await verifying.FindByIdAsync(role.Id!, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Get_and_set_role_id_and_name_accessors_round_trip()
    {
        using var store = CreateStore();
        var role = new SparkRole { Id = "sparkroles/x", Name = "X", NormalizedName = "X" };

        (await store.GetRoleIdAsync(role, CancellationToken.None)).Should().Be("sparkroles/x");
        (await store.GetRoleNameAsync(role, CancellationToken.None)).Should().Be("X");
        (await store.GetNormalizedRoleNameAsync(role, CancellationToken.None)).Should().Be("X");

        await store.SetRoleNameAsync(role, "Y", CancellationToken.None);
        await store.SetNormalizedRoleNameAsync(role, "Y-NORM", CancellationToken.None);

        role.Name.Should().Be("Y");
        role.NormalizedName.Should().Be("Y-NORM");
    }

    [Fact]
    public async Task GetRoleIdAsync_returns_empty_string_when_id_is_null()
    {
        using var store = CreateStore();
        var role = new SparkRole { Name = "Unsaved" };

        (await store.GetRoleIdAsync(role, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddClaimAsync_appends_a_claim_visible_via_GetClaimsAsync()
    {
        using var store = CreateStore();
        var role = new SparkRole { Name = "WithClaims" };

        await store.AddClaimAsync(role, new Claim("permission", "read"));
        await store.AddClaimAsync(role, new Claim("permission", "write"));

        var claims = await store.GetClaimsAsync(role);

        claims.Should().HaveCount(2);
        claims.Select(c => (c.Type, c.Value)).Should().BeEquivalentTo(
        [
            ("permission", "read"),
            ("permission", "write"),
        ]);
    }

    [Fact]
    public async Task RemoveClaimAsync_drops_only_exact_matches()
    {
        using var store = CreateStore();
        var role = new SparkRole
        {
            Name = "WithClaims",
            Claims =
            [
                new() { ClaimType = "permission", ClaimValue = "read" },
                new() { ClaimType = "permission", ClaimValue = "write" },
                new() { ClaimType = "scope", ClaimValue = "read" },
            ]
        };

        await store.RemoveClaimAsync(role, new Claim("permission", "read"));

        var remaining = await store.GetClaimsAsync(role);
        remaining.Should().HaveCount(2);
        remaining.Should().NotContain(c => c.Type == "permission" && c.Value == "read");
    }

    [Fact]
    public async Task RemoveClaimAsync_removes_all_duplicates_of_a_claim()
    {
        using var store = CreateStore();
        var role = new SparkRole
        {
            Name = "Dupes",
            Claims =
            [
                new() { ClaimType = "permission", ClaimValue = "read" },
                new() { ClaimType = "permission", ClaimValue = "read" },
            ]
        };

        await store.RemoveClaimAsync(role, new Claim("permission", "read"));

        (await store.GetClaimsAsync(role)).Should().BeEmpty();
    }

    [Fact]
    public async Task Roles_queryable_returns_stored_roles()
    {
        var creating = CreateStore();
        await creating.CreateAsync(new SparkRole { Name = "A" }, CancellationToken.None);
        await creating.CreateAsync(new SparkRole { Name = "B" }, CancellationToken.None);
        creating.Dispose();

        WaitForIndexing(Store);

        using var reading = CreateStore();
        var roles = await reading.Roles.ToListAsync();
        var names = roles.Select(r => r.Name).ToList();

        names.Should().BeEquivalentTo(["A", "B"]);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var store = CreateStore();
        // Force the lazy session to be created so Dispose actually has something to release.
        _ = store.Roles;

        store.Dispose();
        var act = store.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Operations_after_Dispose_throw_ObjectDisposedException()
    {
        var store = CreateStore();
        store.Dispose();

        var act = async () => await store.CreateAsync(new SparkRole { Name = "Late" }, CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}
