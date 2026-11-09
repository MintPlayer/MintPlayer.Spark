using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// #551 M9: a type with no C# class saves and deletes through its <c>{Name}Actions</c>' own
/// <c>OnSaveAsync(PersistentObject)</c> and <c>OnDeleteAsync(string id, string? etag)</c>, which own the storage. The
/// framework keeps the type-level rights in front of them and writes nothing itself. Without the hooks the type is
/// refused as before (reference add-new PRD D11).
/// </summary>
public class VirtualObjectWriteTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private static readonly Guid NoteTypeId = Guid.Parse("66666666-ffff-ffff-ffff-666666666666");
    private static readonly Guid PageTypeId = Guid.Parse("77777777-aaaa-aaaa-aaaa-777777777777");

    private static EntityTypeFile Model(Guid id, string name) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = name,
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" }],
        }
    };

    private SparkEndpointFactory Factory(VirtualNoteBook book, IPermissionService? permissions = null)
        => new(Store, [Model(NoteTypeId, "VirtualNote"), Model(PageTypeId, "VirtualStartPage")], services =>
        {
            services.AddSingleton(book);
            if (permissions is not null)
            {
                services.RemoveAll<IPermissionService>();
                services.AddSingleton(permissions);
            }
        });

    private static async Task<Abstractions.PersistentObject> NoteAsync(IServiceProvider services, string title)
    {
        var obj = await services.GetRequiredService<IManager>().GetPersistentObjectAsync("VirtualNote");
        obj["Title"].Value = title;
        obj["Title"].IsValueChanged = true;
        return obj;
    }

    [Fact]
    public async Task A_save_runs_the_actions_class_hook_and_returns_its_object()
    {
        var book = new VirtualNoteBook();
        await using var factory = Factory(book);
        await using var scope = factory.GetService<IServiceScopeFactory>().CreateAsyncScope();

        var saved = await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>()
            .SavePersistentObjectAsync(await NoteAsync(scope.ServiceProvider, "Groceries"));

        saved.Id.Should().Be("notes/1");
        book.Notes.Should().ContainKey("notes/1");
        book.Notes["notes/1"].Should().Be("Groceries");
    }

    [Fact]
    public async Task A_delete_runs_the_actions_class_hook_with_the_etag()
    {
        var book = new VirtualNoteBook();
        book.Notes["notes/7"] = "Old";
        await using var factory = Factory(book);
        await using var scope = factory.GetService<IServiceScopeFactory>().CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>()
            .DeletePersistentObjectAsync(NoteTypeId, "notes/7", Abstractions.Interceptors.PersistentObjectOperation.Delete, "v1");

        book.Notes.Should().BeEmpty();
        book.LastDeleteEtag.Should().Be("v1");
    }

    [Fact]
    public async Task The_type_level_right_is_checked_before_the_hook()
    {
        var perms = Substitute.For<IPermissionService>();
        perms.EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(x => Task.FromException(new SparkAccessDeniedException($"{x.ArgAt<string>(0)}/{x.ArgAt<string>(1)}")));
        var book = new VirtualNoteBook();
        book.Notes["notes/7"] = "Old";
        await using var factory = Factory(book, perms);
        await using var scope = factory.GetService<IServiceScopeFactory>().CreateAsyncScope();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var note = await NoteAsync(scope.ServiceProvider, "Groceries");

        var save = await Record.ExceptionAsync(() => databaseAccess.SavePersistentObjectAsync(note));
        var delete = await Record.ExceptionAsync(() => databaseAccess.DeletePersistentObjectAsync(NoteTypeId, "notes/7"));

        save.Should().BeOfType<SparkAccessDeniedException>();
        delete.Should().BeOfType<SparkAccessDeniedException>();
        book.Notes.Keys.Should().Equal(["notes/7"], "neither hook ran");
    }

    /// <summary>A class-less type whose actions class only loads stays unwritable (reference add-new PRD D11).</summary>
    [Fact]
    public async Task A_type_without_write_hooks_is_still_refused()
    {
        await using var factory = Factory(new VirtualNoteBook());
        await using var scope = factory.GetService<IServiceScopeFactory>().CreateAsyncScope();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var page = await scope.ServiceProvider.GetRequiredService<IManager>().GetPersistentObjectAsync("VirtualStartPage");

        var save = await Record.ExceptionAsync(() => databaseAccess.SavePersistentObjectAsync(page));

        save.Should().BeOfType<SparkAccessDeniedException>();
    }
}

/// <summary>The storage the class-less <c>VirtualNote</c> type's actions class writes to.</summary>
public sealed class VirtualNoteBook
{
    public ConcurrentDictionary<string, string?> Notes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastDeleteEtag { get; set; }
    public int NextId;
}

/// <summary>Found by name, like <see cref="VirtualStartPageActions"/>; it owns the type's storage.</summary>
public sealed class VirtualNoteActions : ISparkOwnsRowSecurity
{
    private readonly VirtualNoteBook book;

    public VirtualNoteActions(VirtualNoteBook book) => this.book = book;

    public string RowSecurityRationale => "Test fixture: one shared in-memory note book, no per-caller data.";

    public Task<Abstractions.PersistentObject> OnSaveAsync(Abstractions.PersistentObject obj)
    {
        obj.Id ??= $"notes/{Interlocked.Increment(ref book.NextId)}";
        book.Notes[obj.Id] = obj["Title"].Value?.ToString();
        return Task.FromResult(obj);
    }

    public Task<bool> OnDeleteAsync(string id, string? etag)
    {
        book.LastDeleteEtag = etag;
        return Task.FromResult(book.Notes.TryRemove(id, out _));
    }
}
