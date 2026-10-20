using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Mcp;
using ModelContextProtocol.Protocol;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// MCP live checks, round 1: each row of HR's people query carried the whole nested persistent object of
/// <c>Person.Address</c> (labels in three languages, validation rules) instead of its value. The web grid keeps the
/// object (a custom renderer receives it, #329); an agent's row carries the values only, as <c>get</c> shows a
/// detail object inline.
/// </summary>
public class McpQueryRowShapeTests
{
    [Fact]
    public void A_single_AsDetail_cell_carries_its_values_without_labels_or_rules()
    {
        var address = new PersistentObject
        {
            Name = "Address",
            ObjectTypeId = Guid.NewGuid(),
            Breadcrumb = "Main Street 1, Springfield",
            Attributes =
            [
                new PersistentObjectAttribute
                {
                    Name = "Street",
                    Value = "Main Street 1",
                    Label = TranslatedString.Of("en", "Straatnaam-label"),
                    Rules = [new ValidationRule { Type = "maxLength", Value = 120 }],
                },
                new PersistentObjectAttribute { Name = "City", Value = "Springfield", Label = TranslatedString.Of("en", "Gemeente-label") },
            ],
        };
        var result = new QueryResult
        {
            Columns = [new QueryColumn { Name = "Name" }, new QueryColumn { Name = "Address", AsDetailType = "Address" }],
            Items =
            [
                new QueryResultItem
                {
                    Id = "people/1",
                    Breadcrumb = "Alice",
                    Values =
                    [
                        new QueryResultItemValue { Key = "Name", Value = "Alice" },
                        new QueryResultItemValue { Key = "Address", Value = address, Breadcrumb = address.Breadcrumb },
                    ],
                },
            ],
            TotalItems = 1,
            Skip = 0,
            Take = 10,
        };

        var services = new ServiceCollection();
        services.AddOptions();
        using var provider = services.BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = provider } };
        try
        {
            var text = ((TextContentBlock)SparkMcpResult.Rows(result).Content[0]).Text;

            var cell = McpPlanTests.Unfence(text)["rows"]![0]!["values"]!["Address"]!.AsObject();
            cell["Street"]!.GetValue<string>().Should().Be("Main Street 1");
            cell["City"]!.GetValue<string>().Should().Be("Springfield");
            text.Should().NotContain("Straatnaam-label").And.NotContain("Gemeente-label")
                .And.NotContain("maxLength").And.NotContain("\"rules\"").And.NotContain("\"label\":\"Gemeente");
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
