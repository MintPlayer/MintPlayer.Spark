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
}
