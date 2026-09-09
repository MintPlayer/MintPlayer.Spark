using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the RavenDB behaviour that AsDetail row identity is built on, so a change in the client or
/// its serializer is caught here rather than as data corruption in an application.
/// </summary>
/// <remarks>
/// These are not tests of Spark. They are tests of an assumption Spark makes, and the assumption is
/// load-bearing twice over: <c>docs/prd/PRD-AsDetail-Row-Identity.md</c> keeps the
/// <c>Guid.NewGuid()</c> key initializer because of <see cref="A_stored_row_keeps_its_key"/>, and
/// requires a backfill migration before that key ships because of
/// <see cref="A_row_stored_without_a_key_is_given_a_different_one_on_every_load"/>.
/// <para>
/// The pair matters more than either half. The first says the design works; the second says exactly
/// where it stops working, and that boundary is invisible at runtime — a keyless row does not arrive
/// empty, it arrives with a plausible guid that happens to be new. If the second test ever starts
/// passing for the wrong reason, the migration requirement can be revisited; until then it cannot.
/// </para>
/// </remarks>
public class NestedRowIdentityTests : SparkTestDriver
{
    public class Person
    {
        public string? Id { get; set; }
        public List<Address> Addresses { get; set; } = [];
    }

    /// <summary>Carries a key the way a generated value object does.</summary>
    public class Address
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Street { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
    }

    /// <summary>
    /// The same document shape as <see cref="Person"/>, minus the row key — how every row of the
    /// four types gaining a new <c>Id</c> is stored today.
    /// </summary>
    /// <remarks>
    /// Writing the legacy shape through a keyless CLR type rather than a raw JSON <c>PUT</c> keeps
    /// the test readable and keeps it honest: the JSON really is produced by serialization, not
    /// hand-written to match a guess about what serialization produces.
    /// </remarks>
    public class LegacyPerson
    {
        public string? Id { get; set; }
        public List<LegacyAddress> Addresses { get; set; } = [];
    }

    public class LegacyAddress
    {
        public string Street { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
    }

    /// <summary>
    /// A nested key survives storage untouched, so persistence needs no identity mechanism of its
    /// own — the initializer runs during construction and the stored value overwrites it.
    /// </summary>
    [Fact]
    public async Task A_stored_row_keeps_its_key()
    {
        using var store = GetDocumentStore();

        string personId;
        string[] written;

        using (var session = store.OpenAsyncSession())
        {
            var person = new Person
            {
                Addresses =
                [
                    new() { Street = "Voorbeeldstraat", Number = "231" },
                    new() { Street = "Voorbeeldlaan", Number = "30" },
                ],
            };
            await session.StoreAsync(person);
            await session.SaveChangesAsync();

            personId = person.Id!;
            written = [.. person.Addresses.Select(a => a.Id)];
        }

        // A second session, so the entities are deserialized from the server rather than served
        // from the first session's identity map — which would prove nothing.
        using (var session = store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<Person>(personId);

            loaded.Addresses.Select(a => a.Id).Should().Equal(written,
                "a nested key is an ordinary serialized property, so it must round-trip exactly — "
                + "in the same order, since row matching depends on the value and nothing else");
        }
    }

    /// <summary>
    /// ⚠️ The boundary. A row stored before the key existed does not come back keyless — it comes
    /// back with a <em>fresh</em> guid, different every time, because the field initializer runs
    /// during construction and there is no stored value to overwrite it.
    /// </summary>
    /// <remarks>
    /// This is why a keyless row cannot be detected in memory, why any
    /// <c>string.IsNullOrEmpty(key)</c> guard is unreachable code, and why the backfill migration is
    /// a precondition of shipping the key rather than cleanup afterwards.
    /// </remarks>
    [Fact]
    public async Task A_row_stored_without_a_key_is_given_a_different_one_on_every_load()
    {
        using var store = GetDocumentStore();

        string personId;

        using (var session = store.OpenAsyncSession())
        {
            var legacy = new LegacyPerson
            {
                Addresses =
                [
                    new() { Street = "Voorbeeldstraat", Number = "231" },
                    new() { Street = "Voorbeeldlaan", Number = "30" },
                ],
            };
            await session.StoreAsync(legacy);
            await session.SaveChangesAsync();
            personId = legacy.Id!;
        }

        string[] firstLoad;
        using (var session = store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<Person>(personId);
            firstLoad = [.. loaded.Addresses.Select(a => a.Id)];
        }

        string[] secondLoad;
        using (var session = store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<Person>(personId);
            secondLoad = [.. loaded.Addresses.Select(a => a.Id)];
        }

        firstLoad.Should().NotContain(string.Empty,
            "the initializer runs, so the absence of a stored key is invisible — this is the whole "
            + "problem, and a keyless row would be far easier to handle if this were empty");

        secondLoad.Should().NotEqual(firstLoad,
            "two loads of the same untouched document disagree about row identity, so a stored row "
            + "with no key cannot be matched and must be migrated before the key ships");
    }

    /// <summary>
    /// Loading a keyless document is not a read-only act: the minted keys register as changes, so an
    /// unrelated <c>SaveChanges</c> in the same session writes random ids to a document nobody
    /// edited.
    /// </summary>
    /// <remarks>
    /// This is why the startup gate refuses to start rather than warning. An unmigrated deployment
    /// does not merely mismatch rows — it corrupts them at the first save of anything else.
    /// </remarks>
    [Fact]
    public async Task Loading_a_keyless_document_marks_it_dirty()
    {
        using var store = GetDocumentStore();

        string personId;
        using (var session = store.OpenAsyncSession())
        {
            var legacy = new LegacyPerson { Addresses = [new() { Street = "Voorbeeldstraat", Number = "231" }] };
            await session.StoreAsync(legacy);
            await session.SaveChangesAsync();
            personId = legacy.Id!;
        }

        using (var session = store.OpenAsyncSession())
        {
            await session.LoadAsync<Person>(personId);

            session.Advanced.HasChanges.Should().BeTrue(
                "the minted key counts as a new field, so the document is dirty after a load that "
                + "changed nothing");
        }
    }
}
