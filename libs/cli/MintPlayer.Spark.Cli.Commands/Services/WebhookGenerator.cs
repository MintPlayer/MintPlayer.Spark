using MintPlayer.Spark.Cli.Webhooks;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Services;

public sealed record WebhookGenerateOptions(string Catalog, string Lang, string? Name, string? Namespace, string? Output);

/// <summary><c>spark webhooks generate</c> (D8): read the catalog, emit C# or TypeScript.</summary>
public sealed class WebhookGenerator
{
    public async Task<int> GenerateAsync(WebhookGenerateOptions options, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await ReadCatalogAsync(options.Catalog, cancellationToken);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not read the catalog {options.Catalog}: {e.Message}[/]");
            return 1;
        }

        string code;
        try
        {
            code = Generate(json, options.Lang, options.Name, options.Namespace);
        }
        catch (FormatException e)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{e.Message}[/]");
            return 1;
        }
        catch (ArgumentException e)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{e.Message}[/]");
            return 2;
        }

        if (options.Output is null)
        {
            Console.Out.Write(code);
            return 0;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Output));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(options.Output, code, cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"[green]Wrote {options.Output}[/]");
        return 0;
    }

    /// <summary>The pure part, for tests: catalog JSON → source.</summary>
    public static string Generate(string catalogJson, string lang, string? name, string? @namespace)
    {
        var model = WebhookCatalogModel.Read(catalogJson);
        var baseName = string.IsNullOrWhiteSpace(name) ? DefaultName(model.Title) : WebhookCatalogModel.Pascal(name);
        var codeOptions = new WebhookCodeOptions(baseName, string.IsNullOrWhiteSpace(@namespace) ? "Webhooks" : @namespace.Trim());
        return lang.Trim().ToLowerInvariant() switch
        {
            "cs" or "csharp" or "c#" => CSharpWebhookEmitter.Emit(model, codeOptions),
            "ts" or "typescript" => TypeScriptWebhookEmitter.Emit(model, codeOptions),
            _ => throw new ArgumentException($"--lang is cs or ts, not '{lang}'."),
        };
    }

    /// <summary>"Coverage webhooks" → <c>Coverage</c>; the default title "Webhooks" → <c>Site</c>.</summary>
    internal static string DefaultName(string title)
    {
        var name = WebhookCatalogModel.Pascal(title);
        if (name.EndsWith("Webhooks", StringComparison.Ordinal))
            name = name[..^"Webhooks".Length];
        return name.Length == 0 ? "Site" : name;
    }

    private static async Task<string> ReadCatalogAsync(string catalog, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(catalog, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        return await File.ReadAllTextAsync(catalog, cancellationToken);
    }
}
