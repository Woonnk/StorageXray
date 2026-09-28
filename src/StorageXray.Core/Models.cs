using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace StorageXray.Core;

public static class Sizes
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double n = Math.Abs((double)bytes); int u = 0;
        while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
        return (bytes < 0 ? "−" : "") + n.ToString(u == 0 ? "0" : "0.##") + " " + units[u];
    }
}

public sealed record FileRecord(string Path, long Size, long ModifiedTicks, FileAttributes Attributes, string Category)
{
    [JsonIgnore] public long Id { get; init; }
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    [JsonIgnore] public string SizeText => Sizes.Format(Size);
    [JsonIgnore] public string Modified => new DateTime(ModifiedTicks, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, yyyy");
    [JsonIgnore] public bool CanClean => FilePolicy.IsPersonalFile(Path, Attributes);
}

public sealed record FolderNode(long Id, long ParentId, string Path, long Bytes, long FileCount)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : Path;
    public string SizeText => Sizes.Format(Bytes);
}

public sealed class ScanResult(FileIndex index) : IDisposable
{
    public FileIndex Index { get; } = index;
    public FolderNode Root => Index.GetFolder(1);
    public long FileCount { get; internal set; }
    public long FolderCount { get; internal set; }
    public long Skipped { get; private set; }
    public List<string> SkipDetails { get; } = [];
    public DateTime CompletedUtc { get; set; }
    public bool Cancelled { get; set; }
    public void Skip(string path, string reason)
    {
        lock (SkipDetails)
        {
            Skipped++;
            if (SkipDetails.Count < 100) SkipDetails.Add(path + " — " + reason);
        }
    }
    public void Dispose() => Index.Dispose();
}

public sealed record ScanProgress(long Files, long Bytes, string Current, string Phase);
public sealed record DuplicateResult(long GroupCount, long CopyCount, long ExtraBytes, long Skipped, bool Cancelled);
public sealed record DuplicateRow(long Group, bool Keeper, FileRecord File, string Hash, string KeeperPath)
{ public string Action => Keeper ? "KEEP THIS COPY" : "Extra copy"; }
public enum FileSort { Largest, Smallest, Newest, Oldest, Name }
public sealed record FilePage(IReadOnlyList<FileRecord> Items, long TotalCount, long TotalBytes);
public sealed record FolderEntry(string Name, string Path, long Bytes, string Kind, FolderNode? Folder, FileRecord? File);
public sealed record FolderPage(FolderNode Folder, IReadOnlyList<FolderEntry> Items, long TotalCount);
public sealed record CleanupPage(IReadOnlyList<CleanupItem> Items, long TotalCount);

public sealed class CleanupItem : INotifyPropertyChanged
{
    private bool selected;
    public required FileRecord File { get; init; }
    public required string Reason { get; init; }
    public string? KeeperPath { get; init; }
    public string? ExpectedHash { get; init; }
    public string Name => File.Name;
    public string Path => File.Path;
    public string SizeText => File.SizeText;
    public string Modified => File.Modified;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record FolderChange(string Path, long Before, long After)
{
    public long Delta => After - Before;
    public string ChangeText => (Delta > 0 ? "+" : "") + Sizes.Format(Delta);
    public string BeforeText => Sizes.Format(Before);
    public string AfterText => Sizes.Format(After);
}
