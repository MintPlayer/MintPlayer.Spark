using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Csv;
using MintPlayer.Spark.Csv;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Raven.Client.Documents.Session;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace MintPlayer.Spark.Tests.Csv;

/// <summary>
/// The CSV import and export of persistent objects (warehouse WX11) against substitutes of the read and save paths:
/// what each row becomes (created, updated, skipped, refused and why), the dry run, the natural-id upsert, and the
/// export's columns, ids and injection guard. The save path itself is the one the endpoints use and is tested there.
/// </summary>
public class SparkCsvTransferTests
{
    private static readonly Guid TypeId = Guid.Parse("8d0e4d2b-6c51-4c56-9a9e-3c1b7f0a1c11");

    private readonly IModelLoader models = Substitute.For<IModelLoader>();
    private readonly IQueryReadPath queries = Substitute.For<IQueryReadPath>();
    private readonly IPersistentObjectReadPath reads = Substitute.For<IPersistentObjectReadPath>();
    private readonly IPersistentObjectSavePath saves = Substitute.For<IPersistentObjectSavePath>();
    private readonly IPermissionService permissions = Substitute.For<IPermissionService>();
    private readonly IEntityMapper mapper = Substitute.For<IEntityMapper>();
    private readonly INewInvoker newInvoker = Substitute.For<INewInvoker>();
    private readonly ISparkTypeResolver types = Substitute.For<ISparkTypeResolver>();
    private readonly IAsyncDocumentSession session = Substitute.For<IAsyncDocumentSession>();
    private readonly EntityTypeDefinition person = new() { Id = TypeId, Name = "Person", ClrType = "Tests.Person" };

    public SparkCsvTransferTests()
    {
        models.ResolveEntityType("Person").Returns(person);
        permissions.IsAllowedAsync(Arg.Any<string>(), "Person", Arg.Any<CancellationToken>()).Returns(true);
        mapper.GetPersistentObjectAsync(TypeId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Scaffold());
        saves.CreateAsync(person, Arg.Any<PersistentObject>(), Arg.Any<PersistentObject?>(), Arg.Any<CancellationToken>())
            .Returns(ci => SaveOutcome.Saved(Stored("people/new", ci.ArgAt<PersistentObject>(1))));
        saves.UpdateAsync(person, Arg.Any<string>(), Arg.Any<PersistentObject>(), Arg.Any<MintPlayer.Spark.Abstractions.Retry.IRetryableRequest?>(), Arg.Any<CancellationToken>())
            .Returns(ci => SaveOutcome.Saved(Stored(ci.ArgAt<string>(1), ci.ArgAt<PersistentObject>(2))));
    }

    private SparkCsvTransfer Transfer() => new(models, queries, reads, saves, permissions, mapper, newInvoker, types, session,
        Options.Create(new HttpJsonOptions()));

    private static PersistentObject Scaffold(string? id = null, string? etag = null, string? name = null, long? age = null, string code = "C1") => new()
    {
        Id = id,
        Etag = etag,
        Name = "Person",
        ObjectTypeId = TypeId,
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = name },
            new PersistentObjectAttribute { Name = "Age", DataType = "number", Value = age },
            new PersistentObjectAttribute { Name = "Code", DataType = "string", Value = code, IsReadOnly = true },
        ],
    };

    private static PersistentObject Stored(string id, PersistentObject posted)
    {
        posted.Id = id;
        return posted;
    }

    private static string? Text(PersistentObject obj, string attribute)
        => obj[attribute].Value is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : obj[attribute].Value?.ToString();

    private Task<SparkCsvImportResult> Import(string csv, Action<SparkCsvImportRequest>? configure = null)
    {
        var request = new SparkCsvImportRequest { Type = "Person", Csv = csv };
        configure?.Invoke(request);
        return Transfer().ImportAsync(request);
    }

    // ---------------------------------------------------------------- import: creates

    [Fact]
    public async Task Each_row_is_created_through_the_save_path_and_a_bad_cell_refuses_only_its_row()
    {
        var result = await Import("Name,Age\r\nAnn,30\r\nBob,thirty\r\n");

        result.Error.Should().BeNull();
        result.Created.Should().Be(1);
        result.Refused.Should().Be(1);
        result.Rows[0].Line.Should().Be(2);
        result.Rows[0].Status.Should().Be(SparkCsvRowStatus.Created);
        result.Rows[0].Id.Should().Be("people/new");
        result.Rows[1].Line.Should().Be(3);
        result.Rows[1].Status.Should().Be(SparkCsvRowStatus.Refused);
        result.Rows[1].Errors.Single().Attribute.Should().Be("Age");

        await saves.Received(1).CreateAsync(person, Arg.Is<PersistentObject>(o =>
            Text(o, "Name") == "Ann" && o["Name"].IsValueChanged && Text(o, "Age") == "30"),
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_dry_run_checks_every_row_and_writes_nothing()
    {
        var result = await Import("Name\r\nAnn\r\nBob", r => r.DryRun = true);

        result.DryRun.Should().BeTrue();
        result.Created.Should().Be(2);
        await saves.Received(2).CheckCreateAsync(person, Arg.Any<PersistentObject>(), Arg.Any<CancellationToken>());
        await saves.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_refusal_of_the_save_path_is_reported_on_its_row()
    {
        saves.CreateAsync(person, Arg.Is<PersistentObject>(o => Text(o, "Name") == "Taken"), Arg.Any<PersistentObject?>(), Arg.Any<CancellationToken>())
            .Throws(new SparkValidationException("This name is taken.", "Name"));

        var result = await Import("Name\r\nTaken\r\nFree");

        result.Rows[0].Status.Should().Be(SparkCsvRowStatus.Refused);
        result.Rows[0].Errors.Single().Should().Be(new SparkCsvRowError("Name", "This name is taken."));
        result.Rows[1].Status.Should().Be(SparkCsvRowStatus.Created);
    }

    [Fact]
    public async Task A_denied_row_gets_the_answer_a_missing_row_gets()
    {
        saves.CreateAsync(person, Arg.Any<PersistentObject>(), Arg.Any<PersistentObject?>(), Arg.Any<CancellationToken>())
            .Throws(new SparkAccessDeniedException("Person"));

        var result = await Import("Name\r\nAnn");

        result.Rows.Single().Errors.Single().Message.Should().Be("No such row, or you may not see it.");
    }

    // ---------------------------------------------------------------- import: updates

    [Fact]
    public async Task An_id_column_updates_that_row_at_the_version_loaded()
    {
        reads.LoadAsync(Arg.Is<PersistentObjectReferenceRequest>(r => r.Id == "people/7"), Arg.Any<CancellationToken>())
            .Returns(Scaffold("people/7", "etag-1", "Old", 40));

        var result = await Import("@id,Name,Age\r\npeople/7,New,40");

        result.Updated.Should().Be(1);
        result.Rows.Single().Id.Should().Be("people/7");
        await saves.Received(1).UpdateAsync(person, "people/7", Arg.Is<PersistentObject>(o =>
            o.Etag == "etag-1" && Text(o, "Name") == "New" && o["Name"].IsValueChanged && !o["Age"].IsValueChanged),
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_etag_column_is_the_version_the_update_expects()
    {
        reads.LoadAsync(Arg.Any<PersistentObjectReferenceRequest>(), Arg.Any<CancellationToken>()).Returns(Scaffold("people/7", "etag-2", "Old"));

        await Import("@id,@etag,Name\r\npeople/7,etag-1,New");

        await saves.Received(1).UpdateAsync(person, "people/7", Arg.Is<PersistentObject>(o => o.Etag == "etag-1"), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_row_that_changes_nothing_is_skipped()
    {
        reads.LoadAsync(Arg.Any<PersistentObjectReferenceRequest>(), Arg.Any<CancellationToken>()).Returns(Scaffold("people/7", "e", "Same", 5));

        var result = await Import("@id,Name,Age\r\npeople/7,Same,5");

        result.Skipped.Should().Be(1);
        await saves.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task A_missing_id_is_refused_without_saying_whether_it_exists()
    {
        reads.LoadAsync(Arg.Any<PersistentObjectReferenceRequest>(), Arg.Any<CancellationToken>()).Returns((PersistentObject?)null);

        var result = await Import("@id,Name\r\npeople/404,X");

        result.Rows.Single().Errors.Single().Should().Be(new SparkCsvRowError(SparkCsvColumns.Id, "No such row, or you may not see it."));
    }

    [Fact]
    public async Task Two_records_writing_one_row_refuse_the_second()
    {
        reads.LoadAsync(Arg.Any<PersistentObjectReferenceRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Scaffold("people/7", "e", "Old"));

        var result = await Import("@id,Name\r\npeople/7,A\r\npeople/7,B");

        result.Rows[0].Status.Should().Be(SparkCsvRowStatus.Updated);
        result.Rows[1].Status.Should().Be(SparkCsvRowStatus.Refused);
        result.Rows[1].Errors.Single().Message.Should().Contain("Row 2");
    }

    [Fact]
    public async Task A_read_only_column_is_ignored_when_it_repeats_the_value_and_refused_when_it_changes_it()
    {
        reads.LoadAsync(Arg.Any<PersistentObjectReferenceRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Scaffold("people/7", "e", "Old", code: "C1"));

        var same = await Import("@id,Name,Code\r\npeople/7,New,C1");
        var changed = await Import("@id,Name,Code\r\npeople/7,New,C2");
        var unknown = await Import("@id,Name,Nope\r\npeople/7,New,x");

        same.Rows.Single().Status.Should().Be(SparkCsvRowStatus.Updated);
        changed.Rows.Single().Errors.Single().Should().Be(new SparkCsvRowError("Code", "'Code' is not an attribute you can set on Person."));
        // An attribute that does not exist answers exactly as one the caller may not set.
        unknown.Rows.Single().Errors.Single().Should().Be(new SparkCsvRowError("Nope", "'Nope' is not an attribute you can set on Person."));
    }

    [Fact]
    public async Task Empty_cells_are_ignored_by_default_and_clear_when_asked()
    {
        reads.LoadAsync(Arg.Any<PersistentObjectReferenceRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Scaffold("people/7", "e", "Old", 5));

        var ignored = await Import("@id,Name,Age\r\npeople/7,New,");
        await saves.Received(1).UpdateAsync(person, "people/7", Arg.Is<PersistentObject>(o => !o["Age"].IsValueChanged), null, Arg.Any<CancellationToken>());

        saves.ClearReceivedCalls();
        await Import("@id,Name,Age\r\npeople/7,New,", r => r.EmptyCells = SparkCsvEmptyCells.Clear);
        await saves.Received(1).UpdateAsync(person, "people/7", Arg.Is<PersistentObject>(o => o["Age"].IsValueChanged && o["Age"].Value == null), null, Arg.Any<CancellationToken>());
        ignored.Updated.Should().Be(1);
    }

    // ---------------------------------------------------------------- import: modes and rights

    [Fact]
    public async Task Create_only_refuses_a_row_naming_an_id_and_update_only_a_row_naming_none()
    {
        var createOnly = await Import("@id,Name\r\npeople/7,A", r => r.Mode = SparkCsvImportMode.CreateOnly);
        var updateOnly = await Import("Name\r\nA", r => r.Mode = SparkCsvImportMode.UpdateOnly);

        createOnly.Rows.Single().Errors.Single().Message.Should().Contain("only creates");
        updateOnly.Rows.Single().Errors.Single().Message.Should().Contain("only updates");
    }

    [Fact]
    public async Task A_caller_who_may_neither_create_nor_edit_is_denied_like_an_unknown_type()
    {
        permissions.IsAllowedAsync(Arg.Any<string>(), "Person", Arg.Any<CancellationToken>()).Returns(false);

        Func<Task> denied = () => Import("Name\r\nA");
        Func<Task> unknown = () => Transfer().ImportAsync(new SparkCsvImportRequest { Type = "Nope", Csv = "Name\r\nA" });

        await denied.Should().ThrowAsync<SparkAccessDeniedException>();
        await unknown.Should().ThrowAsync<SparkAccessDeniedException>();
    }

    [Fact]
    public async Task A_file_spark_cannot_read_is_refused_as_a_whole()
    {
        (await Import("")).Error.Should().Be("The file is empty.");
        (await Import("Name,@secret\r\nA,b")).Error.Should().Contain("@secret");
        var tooMany = new StringBuilder("Name\r\n");
        for (var i = 0; i <= SparkCsvTransfer.MaxImportRows; i++)
            tooMany.Append("n").Append(i).Append("\r\n");
        (await Import(tooMany.ToString())).Error.Should().Contain("at most");
        await saves.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_semicolon_file_with_a_bom_and_a_header_mapping_imports()
    {
        var result = await Import("﻿Naam;Leeftijd\r\n\"Peeters; An\";31\r\n",
            r => r.Mapping = new() { ["Naam"] = "Name", ["Leeftijd"] = "Age" });

        result.Created.Should().Be(1);
        await saves.Received(1).CreateAsync(person, Arg.Is<PersistentObject>(o => Text(o, "Name") == "Peeters; An"), null, Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------- import: natural ids

    private sealed class Coded : IHasNaturalId
    {
        public string? Code { get; set; }
        public string GetId() => "coded/" + Code;
    }

    [Fact]
    public async Task A_natural_id_type_updates_the_row_holding_the_key_and_creates_the_others()
    {
        types.Resolve("Tests.Person").Returns(typeof(Coded));
        mapper.ToEntity(Arg.Any<PersistentObject>()).Returns(ci => new Coded { Code = Text(ci.ArgAt<PersistentObject>(0), "Name") });
        reads.LoadAsync(Arg.Is<PersistentObjectReferenceRequest>(r => r.Id == "coded/A"), Arg.Any<CancellationToken>())
            .Returns(_ => Scaffold("coded/A", "e", "A", 1));

        var result = await Import("Name,Age\r\nA,2\r\nB,3\r\nB,4");

        result.Rows.Select(r => r.Status).Should().Equal(SparkCsvRowStatus.Updated, SparkCsvRowStatus.Created, SparkCsvRowStatus.Refused);
        result.Rows[0].Id.Should().Be("coded/A");
        result.Rows[2].Errors.Single().Message.Should().Contain("Row 3");
        await saves.Received(1).UpdateAsync(person, "coded/A", Arg.Any<PersistentObject>(), null, Arg.Any<CancellationToken>());
        await saves.Received(1).CreateAsync(person, Arg.Any<PersistentObject>(), null, Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------- export

    private static QueryResult Page(params QueryResultItem[] items) => new()
    {
        Columns =
        [
            new QueryColumn { Name = "Manager", DataType = "Reference", Order = 2 },
            new QueryColumn { Name = "Name", Order = 0 },
            new QueryColumn { Name = "Salary", DataType = "decimal", Order = 1 },
        ],
        Items = items,
        TotalItems = items.Length,
        Skip = 0,
        Take = 1000,
    };

    private static QueryResultItem Row(string id, object? name, object? salary, string? managerId) => new()
    {
        Id = id,
        Etag = "e-" + id,
        Values =
        [
            new QueryResultItemValue { Key = "Name", Value = name },
            new QueryResultItemValue { Key = "Salary", Value = salary },
            new QueryResultItemValue { Key = "Manager", Value = managerId, ObjectId = managerId, Breadcrumb = "The Boss" },
        ],
    };

    [Fact]
    public async Task An_export_writes_ids_columns_in_order_references_as_ids_and_guards_formulas()
    {
        queries.ExecuteAsync(Arg.Any<ExecuteQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(QueryReadOutcome.Ok(Page(Row("people/1", "=SUM(A1)", -1234.5m, "people/9"), Row("people/2", "Ann; Bob", null, null))));

        var file = await Transfer().ExportAsync(new SparkCsvExportRequest { QueryId = "GetPeople", Delimiter = ';' });

        file.Should().NotBeNull();
        file!.FileName.Should().Be("GetPeople.csv");
        file.Rows.Should().Be(2);
        file.Content.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(file.Content[3..]).Should().Be(
            "@id;@etag;Name;Salary;Manager\r\n" +
            "people/1;e-people/1;'=SUM(A1);-1234.5;people/9\r\n" +
            "people/2;e-people/2;\"Ann; Bob\";;\r\n");
    }

    [Fact]
    public async Task An_export_pages_through_the_whole_query()
    {
        var first = Page(Row("people/1", "A", 1m, null));
        queries.ExecuteAsync(Arg.Is<ExecuteQueryRequest>(r => r.Skip == 0), Arg.Any<CancellationToken>())
            .Returns(QueryReadOutcome.Ok(new QueryResult { Columns = first.Columns, Items = first.Items, TotalItems = 2, Skip = 0, Take = 1 }));
        queries.ExecuteAsync(Arg.Is<ExecuteQueryRequest>(r => r.Skip == 1), Arg.Any<CancellationToken>())
            .Returns(QueryReadOutcome.Ok(new QueryResult { Columns = first.Columns, Items = [Row("people/2", "B", 2m, null)], TotalItems = 2, Skip = 1, Take = 1 }));

        var file = await Transfer().ExportAsync(new SparkCsvExportRequest { QueryId = "q", IncludeId = false, IncludeBom = false });

        file!.Rows.Should().Be(2);
        Encoding.UTF8.GetString(file.Content).Should().Be("Name,Salary,Manager\r\nA,1,\r\nB,2,\r\n");
    }

    [Fact]
    public async Task An_unknown_or_unreadable_query_exports_nothing()
    {
        queries.ExecuteAsync(Arg.Any<ExecuteQueryRequest>(), Arg.Any<CancellationToken>()).Returns(QueryReadOutcome.NotFound("Query not found"));

        (await Transfer().ExportAsync(new SparkCsvExportRequest { QueryId = "secret" })).Should().BeNull();
    }

    [Fact]
    public void Export_file_names_keep_only_safe_characters() =>
        SparkCsvTransfer.FileName("../evil name\r\n").Should().Be("evil-name.csv");
}
