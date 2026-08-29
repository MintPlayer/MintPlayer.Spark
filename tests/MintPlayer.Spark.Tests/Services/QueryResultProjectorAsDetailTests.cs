using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using System.Text.Json;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Issue #329 — what a query row carries for an <c>AsDetail</c> column.
/// </summary>
/// <remarks>
/// The regression these pin was invisible to the compiler and to every existing test:
/// <see cref="QueryResultItemValue.Value"/> is <c>object?</c>, so nulling a single child
/// type-checks, serialises, and reaches the browser as a well-formed cell. Only the renderer —
/// which is handed <c>value</c> and nothing else on a grid — could tell, and its null fallback
/// painted an empty column silently.
/// <para>
/// So the assertions here are on the <b>wire</b> as well as the object: a projected single child
/// must serialise to the same <c>{ attributes: [{ name, value }] }</c> shape the detail page
/// sends, because that is the shape <c>guide-custom-attribute-renderers.md</c> promises and the
/// only shape a renderer written against a detail field already understands.
/// </para>
/// </remarks>
public class QueryResultProjectorAsDetailTests
{
    private static readonly Guid RowTypeId = Guid.Parse("dddd1111-1111-1111-1111-dddd11111111");
    private static readonly Guid ChildTypeId = Guid.Parse("dddd2222-2222-2222-2222-dddd22222222");

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static QueryColumn Column(bool isArray) => new()
    {
        Name = "Coverage",
        DataType = "AsDetail",
        IsArray = isArray,
        AsDetailType = "Tests.Entities.CoverageSummary",
        Renderer = "coverage-bar",
    };

    private static PersistentObject Child(int linesCovered) => new()
    {
        Name = "CoverageSummary",
        ObjectTypeId = ChildTypeId,
        Breadcrumb = linesCovered.ToString(),
        Attributes =
        [
            new PersistentObjectAttribute { Name = "LinesCovered", DataType = "int", Value = linesCovered },
            new PersistentObjectAttribute { Name = "LinesCoverable", DataType = "int", Value = 3427 },
        ],
    };

    private static PersistentObject Row(PersistentObjectAttributeAsDetail coverage) => new()
    {
        Id = "Commits/1",
        Name = "Commit",
        ObjectTypeId = RowTypeId,
        Attributes = [coverage],
    };

    private static QueryResultItemValue Project(QueryColumn column, PersistentObjectAttributeAsDetail coverage)
        => QueryResultProjector.ToItems([Row(coverage)], [column], "Commits")[0].Values[0];

    [Fact]
    public void Single_child_projects_the_nested_object_as_the_cell_value()
    {
        var cell = Project(Column(isArray: false), new PersistentObjectAttributeAsDetail
        {
            Name = "Coverage",
            DataType = "AsDetail",
            Object = Child(1422),
        });

        cell.Value.Should().BeOfType<PersistentObject>(
            "a renderer on a single-child AsDetail column receives the nested object — the grid has " +
            "no second channel to reach it through (#329)");
        cell.Value.As<PersistentObject>().Attributes
            .Should().Contain(a => a.Name == "LinesCovered");
    }

    [Fact]
    public void Single_child_keeps_its_resolved_breadcrumb_beside_the_object()
    {
        var cell = Project(Column(isArray: false), new PersistentObjectAttributeAsDetail
        {
            Name = "Coverage",
            DataType = "AsDetail",
            Object = Child(1422),
        });

        cell.Breadcrumb.Should().Be("1422",
            "a rendererless cell prints the breadcrumb and never looks at the value, so carrying the " +
            "object must not disturb it");
    }

    [Fact]
    public void Single_child_absent_projects_null_rather_than_a_scaffold()
    {
        var cell = Project(Column(isArray: false), new PersistentObjectAttributeAsDetail
        {
            Name = "Coverage",
            DataType = "AsDetail",
            Object = null,
            Breadcrumb = "—",
        });

        cell.Value.Should().BeNull();
        cell.Breadcrumb.Should().Be("—");
    }

    [Fact]
    public void Array_projects_the_child_count()
    {
        var cell = Project(Column(isArray: true), new PersistentObjectAttributeAsDetail
        {
            Name = "Coverage",
            DataType = "AsDetail",
            IsArray = true,
            Objects = [Child(1), Child(2), Child(3)],
        });

        cell.Value.Should().Be(3, "#327's `3 items` cell is unaffected by the single-child fix");
    }

    [Fact]
    public void Array_with_no_children_projects_zero()
    {
        var cell = Project(Column(isArray: true), new PersistentObjectAttributeAsDetail
        {
            Name = "Coverage",
            DataType = "AsDetail",
            IsArray = true,
            Objects = null,
        });

        cell.Value.Should().Be(0);
    }

    /// <summary>
    /// The assertion that would have caught the regression: the C# object graph can be right and
    /// the wire still wrong, since the cell value is <c>object?</c> and STJ decides its shape from
    /// the runtime type.
    /// </summary>
    [Fact]
    public void Single_child_serialises_to_the_shape_a_detail_page_sends()
    {
        var cell = Project(Column(isArray: false), new PersistentObjectAttributeAsDetail
        {
            Name = "Coverage",
            DataType = "AsDetail",
            Object = Child(1422),
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(cell, WireOptions));

        var attributes = document.RootElement.GetProperty("value").GetProperty("attributes");
        attributes.EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .Should().Contain("LinesCoverable");
        attributes.EnumerateArray()
            .First(a => a.GetProperty("name").GetString() == "LinesCovered")
            .GetProperty("value").GetInt32().Should().Be(1422);
    }

    /// <summary>
    /// Generalises past AsDetail: a column that declares a renderer and whose attribute carries
    /// data must never project a null value, whatever the data type. A renderer's only input on a
    /// grid is the cell value, so a null there is a blank column by construction.
    /// </summary>
    [Fact]
    public void No_populated_column_carrying_a_renderer_projects_a_null_value()
    {
        var columns = new[]
        {
            Column(isArray: false),
            new QueryColumn
            {
                Name = "Coverages",
                DataType = "AsDetail",
                IsArray = true,
                AsDetailType = "Tests.Entities.CoverageSummary",
                Renderer = "coverage-bar",
            },
            new QueryColumn { Name = "Delta", DataType = "decimal", Renderer = "trend" },
        };

        var row = new PersistentObject
        {
            Id = "Commits/1",
            Name = "Commit",
            ObjectTypeId = RowTypeId,
            Attributes =
            [
                new PersistentObjectAttributeAsDetail { Name = "Coverage", DataType = "AsDetail", Object = Child(1422) },
                new PersistentObjectAttributeAsDetail { Name = "Coverages", DataType = "AsDetail", IsArray = true, Objects = [Child(1)] },
                new PersistentObjectAttribute { Name = "Delta", DataType = "decimal", Value = -0.338m },
            ],
        };

        var values = QueryResultProjector.ToItems([row], columns, "Commits")[0].Values;

        values.Where(v => columns.First(c => c.Name == v.Key).Renderer is not null)
            .Should().OnlyContain(v => v.Value != null);
    }
}
