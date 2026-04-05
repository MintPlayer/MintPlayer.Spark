namespace SparkEditor.Services;

public class FileChangedEventArgs : EventArgs
{
    public required string FilePath { get; set; }
    public WatcherChangeTypes ChangeType { get; set; }
}
