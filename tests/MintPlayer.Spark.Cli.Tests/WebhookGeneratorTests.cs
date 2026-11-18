using System.Net;
using System.Net.Sockets;
using MintPlayer.Spark.Cli.Commands;
using MintPlayer.Spark.Cli.Services;
using MintPlayer.Spark.Cli.Webhooks;

namespace MintPlayer.Spark.Cli.Tests;

/// <summary>
/// #691 D8 (<c>docs/apps_webhooks_PRD.md</c>): <c>spark webhooks generate</c>, from the catalog's odd corners (unions, maps,
/// nullable references, colliding names) to where it reads the catalog from and where it writes the code.
/// </summary>
[Collection(nameof(ConsoleCollection))]
public class WebhookGeneratorTests
{
    /// <summary>A catalog with the schema shapes the model reads; <c>broken.ref</c> is added per test.</summary>
    private const string Catalog = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Shop webhooks", "version": "2.0.0" },
          "webhooks": {
            "no.post": { "get": {} },
            "shape.tested": {
              "post": {
                "summary": "shape.tested",
                "x-spark-permission": "shapes:read",
                "requestBody": { "content": { "application/json": { "schema": { "properties": { "data": {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string", "format": "uuid" },
                    "done": { "type": "boolean" },
                    "tags": { "type": "object", "additionalProperties": { "type": "integer" } },
                    "bag": { "type": "object" },
                    "maybeNames": { "type": "array", "items": { "type": ["string", "null"] } },
                    "anything": { "type": ["string", "integer"] },
                    "odd": { "type": "mystery" },
                    "untyped": {},
                    "parent": { "$ref": "#", "type": ["object", "null"] },
                    "firstOf": { "$ref": "#/properties/list/items/0" },
                    "list": { "type": "array", "items": [ { "type": "number" } ] },
                    "a-b": { "type": "string" },
                    "aB": { "type": "string" },
                    "a_b": { "type": "string" },
                    "---": { "type": "string" },
                    "9lives": { "type": "string" },
                    "nullableRefs": { "type": "array", "items": { "$ref": "#/properties/tags", "type": ["object", "null"] } },
                    "nullableAny": { "$ref": "#/properties/anything", "type": ["object", "null"] },
                    "nullableList": { "$ref": "#/properties/maybeNames", "type": ["object", "null"] },
                    "nullableDone": { "$ref": "#/properties/done", "type": ["object", "null"] }
                  }
                } } } } } }
              }
            }
          }
        }
        """;

    [Fact]
    public void The_model_reads_every_shape_and_names_colliding_properties_apart()
    {
        var model = WebhookCatalogModel.Read(Catalog);

        model.Title.Should().Be("Shop webhooks");
        model.Version.Should().Be("2.0.0");
        var e = model.Events.Should().ContainSingle("an item without post is not an event").Which;
        e.Summary.Should().BeNull("a summary that only repeats the name says nothing");
        e.Permission.Should().Be("shapes:read");
        var data = model.Types.OfType<WebhookObjectType>().Single(t => t.Name == "ShapeTested");
        var byWire = data.Properties.ToDictionary(p => p.WireName);

        byWire["tags"].Type.Should().Be(new WebhookMap(new WebhookPrimitive("integer", null, false), false));
        byWire["bag"].Type.Should().Be(new WebhookMap(new WebhookAny(false), false));
        byWire["anything"].Type.Should().Be(new WebhookAny(false), "a union is raw JSON");
        byWire["odd"].Type.Should().Be(new WebhookAny(false), "a type the model does not know is raw JSON");
        byWire["untyped"].Type.Should().Be(new WebhookAny(false));
        byWire["parent"].Type.Should().Be(new WebhookNamed("ShapeTested", true), "# is the data root: a recursive, nullable reference");
        byWire["firstOf"].Type.Should().Be(new WebhookPrimitive("number", null, false), "a pointer can index an array");
        byWire["nullableRefs"].Type.Should().Be(new WebhookArray(new WebhookMap(new WebhookPrimitive("integer", null, false), true), false));
        byWire["nullableAny"].Type.Should().Be(new WebhookAny(true));
        byWire["nullableList"].Type.Should().Be(new WebhookArray(new WebhookPrimitive("string", null, true), true));
        byWire["nullableDone"].Type.Should().Be(new WebhookPrimitive("boolean", null, true));
        new[] { byWire["a-b"].Name, byWire["aB"].Name, byWire["a_b"].Name }.Should().Equal(["AB", "ABValue", "AB2"]);
        byWire["---"].Name.Should().Be("Value");
        byWire["9lives"].Name.Should().Be("_9lives");
    }

    [Fact]
    public void Both_emitters_write_every_shape()
    {
        var csharp = WebhookGenerator.Generate(Catalog, "csharp", null, null);
        csharp.Should().Contain("namespace Webhooks").And.Contain("ShopWebhooksProcessor");
        csharp.Should().Contain("Guid").And.Contain("bool").And.Contain("Dictionary<string, long>").And.Contain("List<string?>");

        var typescript = WebhookGenerator.Generate(Catalog, " TS ", "my shop", "Ignored");
        typescript.Should().Contain("MyShop").And.Contain("boolean").And.Contain("Record<string, number>").And.Contain("(string | null)[]");
    }

    [Fact]
    public void A_catalog_that_is_not_one_and_an_unknown_language_are_refused()
    {
        FluentThrow(() => WebhookCatalogModel.Read("{ not json")).Should().BeOfType<FormatException>();
        FluentThrow(() => WebhookCatalogModel.Read("""{ "openapi": "3.1.0" }""")).Should().BeOfType<FormatException>();
        var broken = Catalog.Replace("\"untyped\": {}", "\"untyped\": { \"$ref\": \"elsewhere.json#/x\" }");
        FluentThrow(() => WebhookCatalogModel.Read(broken)).Should().BeOfType<FormatException>("only pointers into the data schema resolve");
        FluentThrow(() => WebhookGenerator.Generate(Catalog, "java", null, null)).Should().BeOfType<ArgumentException>();
    }

    [Fact]
    public void The_default_name_comes_from_the_title()
    {
        WebhookGenerator.DefaultName("Coverage webhooks").Should().Be("Coverage");
        WebhookGenerator.DefaultName("Webhooks").Should().Be("Site");
        WebhookGenerator.DefaultName("Shop").Should().Be("Shop");
    }

    [Fact]
    public async Task Generate_reads_a_file_writes_a_file_and_answers_1_or_2_when_it_cannot()
    {
        var directory = Directory.CreateTempSubdirectory("spark-webhooks-");
        try
        {
            var catalog = Path.Combine(directory.FullName, "catalog.json");
            await File.WriteAllTextAsync(catalog, Catalog);
            var output = Path.Combine(directory.FullName, "out", "ShopWebhooks.cs");
            var generator = new WebhookGenerator();

            (await generator.GenerateAsync(new(catalog, "cs", null, "Shop.Hooks", output), CancellationToken.None)).Should().Be(0);
            (await File.ReadAllTextAsync(output)).Should().Contain("namespace Shop.Hooks", "the output directory is created");

            (await generator.GenerateAsync(new(Path.Combine(directory.FullName, "missing.json"), "cs", null, null, null), CancellationToken.None))
                .Should().Be(1, "a catalog that cannot be read");
            var notJson = Path.Combine(directory.FullName, "bad.json");
            await File.WriteAllTextAsync(notJson, "nope");
            (await generator.GenerateAsync(new(notJson, "cs", null, null, null), CancellationToken.None)).Should().Be(1, "a catalog that is not one");
            (await generator.GenerateAsync(new(catalog, "java", null, null, null), CancellationToken.None)).Should().Be(2, "a usage error");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Generate_fetches_an_http_catalog_and_writes_to_the_standard_output_by_default()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = System.Text.Encoding.UTF8.GetBytes(Catalog);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        var written = new StringWriter();
        var original = Console.Out;
        Console.SetOut(written);
        int exit;
        try
        {
            exit = await new WebhookGenerator().GenerateAsync(new($"http://localhost:{port}/spark/apps/webhooks/catalog", "ts", null, null, null), CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }
        await serving;

        exit.Should().Be(0);
        written.ToString().Should().Contain("ShapeTested", "the code goes to the standard output");
    }

    [Fact]
    public async Task The_commands_print_usage_and_pass_their_options_on()
    {
        (await new WebhooksCommand().Execute(CancellationToken.None)).Should().Be(1, "the group command only prints its usage");

        var command = new WebhooksGenerateCommand(new WebhookGenerator()) { Catalog = "missing.json", Lang = "cs", Name = "N", Namespace = "NS", Output = null };
        (await command.Execute(CancellationToken.None)).Should().Be(1, "the options reach the generator, which cannot read the catalog");
    }

    private static Exception? FluentThrow(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}

/// <summary>Tests that redirect <see cref="Console.Out"/> run one at a time.</summary>
[CollectionDefinition(nameof(ConsoleCollection), DisableParallelization = true)]
public sealed class ConsoleCollection;
