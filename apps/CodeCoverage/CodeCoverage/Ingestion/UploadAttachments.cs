namespace CodeCoverage.Ingestion;

/// <summary>Attachment naming on Build documents — one place owns the scheme.</summary>
public static class UploadAttachments
{
    public static string ReportName(string sessionId, int index, string originalFileName)
    {
        // Attachment names must be safe regardless of what the uploader called the file.
        var safe = new string(originalFileName
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')
            .ToArray());
        return $"sessions/{sessionId}/{index}-{safe}";
    }

    public static string FileListName(string sessionId) => $"sessions/{sessionId}/filelist";

    /// <summary>
    /// The uploaded file's name as the consumer would recognise it, recovered from an
    /// attachment name for reporting a rejection (#417). Strips the
    /// <c>sessions/{id}/{index}-</c> prefix that <see cref="ReportName"/> adds; the
    /// sanitisation it applies is not reversible, so this is a best-effort label for
    /// a human, never a key.
    /// </summary>
    public static string DisplayName(string attachmentName)
    {
        var lastSlash = attachmentName.LastIndexOf('/');
        var tail = lastSlash < 0 ? attachmentName : attachmentName[(lastSlash + 1)..];

        var dash = tail.IndexOf('-');
        if (dash > 0 && tail[..dash].All(char.IsAsciiDigit))
            tail = tail[(dash + 1)..];

        return tail.Length == 0 ? attachmentName : tail;
    }
}
