using System.Collections.Concurrent;

namespace StorageXray.Core;

public sealed record ScanOptions(int MaxParallelism = 4, string? CacheDirectory = null);

public sealed class Scanner
{
    internal Func<string, IEnumerator<FileSystemInfo>> Enumerate { get; init; } = path => new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator();
    private sealed class Frame(long id, string path, int depth, IEnumerator<FileSystemInfo> entries) : IDisposable
    {
        public long Id { get; } = id;
        public string Path { get; } = path;
        public int Depth { get; } = depth;
        public IEnumerator<FileSystemInfo> Entries { get; } = entries;
        public long Bytes, Files;
        public void Dispose() => Entries.Dispose();
    }
    private sealed record Entry(string Path, bool Directory, FileRecord? File);

    public ScanResult Scan(string path, IProgress<ScanProgress>? progress, CancellationToken cancellation, ScanOptions? options = null)
    {
        options ??= new();
        if (options.MaxParallelism is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(options), "Use one to eight scan workers.");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (FilePolicy.IsCloudPath(path)) throw new IOException("Cloud-sync folders are excluded from scanning.");
        FilePolicy.CheckAncestors(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Choose an existing drive or folder.");
        var index = new FileIndex(options.CacheDirectory);
        var result = new ScanResult(index);
        long files = 0, bytes = 0, lastReport = 0;
        var reportGate = new object();
        bool IsIndexPath(string p) => IsInside(p, index.DirectoryPath) || IsInside(p, FileIndex.CacheRoot);
        bool SkipPath(string p, string name) => FilePolicy.IsCloudPath(p) || IsIndexPath(p) || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        void Report(string current, bool final = false)
        {
            lock (reportGate)
            {
                long count = Interlocked.Read(ref files);
                if (!final && count - lastReport < 500) return;
                lastReport = count;
                progress?.Report(new(count, Interlocked.Read(ref bytes), current, final ? (result.Cancelled ? "Scan stopped" : "Scan complete") : "Scanning"));
            }
        }
        Frame Open(long id, string p, int depth)
        {
            try
            {
                // Check every subtree boundary before creating its enumerator.
                if (SkipPath(p, Path.GetFileName(p)) || (File.GetAttributes(p) & FilePolicy.UnavailableAttributes) != 0)
                    throw new IOException("Excluded cloud, linked, or app-index subtree");
                return new(id, p, depth, Enumerate(p));
            }
            catch (Exception e) when (IsFilesystemError(e))
            { result.Skip(p, e.Message); return new(id, p, depth, Enumerable.Empty<FileSystemInfo>().GetEnumerator()); }
        }
        Entry? Next(Frame frame)
        {
            // A lazy enumeration or metadata failure ends this folder's branch.
            // Already recorded entries remain, and the skip is made visible.
            try
            {
                while (frame.Entries.MoveNext())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entry = frame.Entries.Current;
                    if (SkipPath(entry.FullName, entry.Name)) { result.Skip(entry.FullName, "excluded cloud or managed subtree"); continue; }
                    var attributes = entry.Attributes;
                    if ((attributes & FilePolicy.UnavailableAttributes) != 0) { result.Skip(entry.FullName, "link or cloud-only item"); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) return new(entry.FullName, true, null);
                    var info = (FileInfo)entry;
                    return new(info.FullName, false, new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, attributes, FilePolicy.Category(info.FullName)));
                }
            }
            catch (Exception e) when (IsFilesystemError(e)) { result.Skip(frame.Path, e.Message); }
            return null;
        }
        void Record(Frame frame, FileRecord file)
        {
            index.AddFile(frame.Id, file); // Index failures must abort the scan.
            frame.Bytes += file.Size; frame.Files++;
            Interlocked.Add(ref bytes, file.Size);
            if (Interlocked.Increment(ref files) % 500 == 0) Report(frame.Path);
        }
        void Traverse(FolderNode branch)
        {
            var stack = new Stack<Frame>(); stack.Push(Open(branch.Id, branch.Path, 1));
            void Finish()
            {
                using var frame = stack.Pop(); index.FinishFolder(frame.Id, frame.Bytes, frame.Files);
                if (stack.TryPeek(out var parent)) { parent.Bytes += frame.Bytes; parent.Files += frame.Files; }
            }
            try
            {
                while (stack.TryPeek(out var frame))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entry = Next(frame);
                    if (entry == null) { Finish(); continue; }
                    if (!entry.Directory) { Record(frame, entry.File!); continue; }
                    long id = index.AddFolder(entry.Path, frame.Id, frame.Depth + 1);
                    stack.Push(Open(id, entry.Path, frame.Depth + 1));
                }
            }
            finally { while (stack.Count > 0) Finish(); }
        }
        try
        {
            long rootId = index.AddFolder(path, 0, 0);
            using var root = Open(rootId, path, 0);
            try
            {
                // Stage top-level folders on disk, not in an unbounded work list.
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entry = Next(root); if (entry == null) break;
                    if (entry.Directory) index.AddFolder(entry.Path, rootId, 1); else Record(root, entry.File!);
                }
                index.Flush();
                Parallel.ForEach(Partitioner.Create(index.TopLevelFolders(), EnumerablePartitionerOptions.NoBuffering),
                    new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = cancellation }, Traverse);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { result.Cancelled = true; }
            index.CompleteRoot(root.Bytes, root.Files);
            result.FileCount = files; result.FolderCount = index.FolderCount();
            // Finalize partial scans as well, for consistent paging and export.
            progress?.Report(new(files, bytes, path, "Indexing scan results"));
            index.BuildIndexes();
            if (cancellation.IsCancellationRequested) result.Cancelled = true;
            result.CompletedUtc = DateTime.UtcNow; Report(path, true); return result;
        }
        catch { result.Dispose(); throw; }
    }
    private static bool IsInside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsFilesystemError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
