using System.IO.Compression;
using System.Text;
using CodeCoverage.Entities;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>Reads an upload attachment as text, transparently un-gzipping (the action gzips by default).</summary>
public static class BuildAttachments
{
    public static async Task<string?> ReadTextAsync(IAsyncDocumentSession session, Build build, string name, CancellationToken cancellationToken)
    {
        var attachment = await session.Advanced.Attachments.GetAsync(build, name, cancellationToken);
        if (attachment is null) return null;

        await using var stream = attachment.Stream;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
