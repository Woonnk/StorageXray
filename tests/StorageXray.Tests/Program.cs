using System.Text;
using StorageXray.Core;

int passed = 0, failed = 0;
void Test(string name, Action<Fixture> test)
{
    using var fixture = new Fixture();
    try { test(fixture); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
}
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Throws(Action action, string message) { try { action(); } catch (IOException) { return; } throw new Exception(message); }

Test("Scanner aggregates nested files and empty folders without losing bytes", f =>
{
    f.Write("Downloads/a.zip", 70000, 1); f.Write("Pictures/sub/a.png", 99000, 2); f.Write("Pictures/b.png", 200, 3);
    Directory.CreateDirectory(Path.Combine(f.Root, "empty"));
    var scan = f.Scan();
    Assert(scan.Root.Bytes == 169200 && scan.FileCount == 3, "Wrong total");
    Assert(scan.Index.ChildFolders(1).Single(n => n.Name == "Pictures").Bytes == 99200, "Wrong nested total");
    Assert(scan.Root.FileCount == 3 && !scan.Cancelled, "Wrong recursive count");
});
Test("Scanner skips symlinks and cannot escape the selected tree", f =>
{
    f.Write("real/a.txt", 40, 1);
    Directory.CreateSymbolicLink(Path.Combine(f.Root, "linked"), Path.Combine(f.Root, "real"));
    var scan = f.Scan();
    Assert(scan.Root.Bytes == 40 && scan.FileCount == 1 && scan.Skipped == 1, "Followed or double-counted a link");
    Throws(() => new Scanner().Scan(Path.Combine(f.Root, "linked"), null, default), "Accepted a linked scan root");
});
Test("Cancelled scan reports partial state", f =>
{
    f.Write("a.txt", 100, 1);
    using var c = new CancellationTokenSource(); c.Cancel();
    using var scan = new Scanner().Scan(f.Root, null, c.Token);
    Assert(scan.Cancelled && scan.FileCount == 0, "Cancellation was ignored");
});
Test("Full-file hashing distinguishes same-size files with matching samples", f =>
{
    string a = f.Write("Pictures/a.png", 131072, 7); string b = f.Copy(a, "Downloads/b.png");
    string c = f.Copy(a, "Pictures/different.png");
    var content = File.ReadAllBytes(c); content[60000] ^= 255; File.WriteAllBytes(c, content);
    var duplicates = new DuplicateFinder().Find(f.Scan().Index, null, default);
    Assert(duplicates.GroupCount == 1 && duplicates.CopyCount == 2, "False duplicate match");
    Assert(f.LastScan!.Index.DuplicatePage().Single(r => r.Keeper).File.Path == a && duplicates.ExtraBytes == 131072, "Wrong keeper or extra bytes");
});
Test("System, app, cloud, and tiny files are excluded from duplicates", f =>
{
    string a = f.Write("Documents/a.pdf", 70000, 5);
    f.Copy(a, "Windows/b.pdf"); f.Copy(a, "AppData/c.pdf"); f.Copy(a, "OneDrive/d.pdf");
    string tiny = f.Write("Downloads/small.txt", 40, 3); f.Copy(tiny, "Pictures/copy.txt");
    Assert(new DuplicateFinder().Find(f.Scan().Index, null, default).GroupCount == 0, "Excluded files matched");
});
Test("Planner does not also suggest the preserved duplicate copy", f =>
{
    string a = f.Write("Downloads/a.zip", 150000, 9, 100); f.Copy(a, "Downloads/b.zip");
    f.Write("Downloads/unique.zip", 80000, 15, 100); f.Write("Downloads/recent.zip", 90000, 16, 1);
    var scan = f.Scan(); var dup = new DuplicateFinder().Find(scan.Index, null, default);
    var plan = CleanupPlanner.Candidates(scan, DateTime.UtcNow).Items;
    Assert(plan.Count == 2 && plan.All(i => i.Path != scan.Index.DuplicatePage().Single(r => r.Keeper).File.Path), "Keeper was included in cleanup");
    long selected = CleanupPlanner.SelectGoal(plan.ToList(), 140000);
    Assert(selected == 150000 && plan.Count(i => i.Selected) == 1 && plan.Single(i => i.Selected).ExpectedHash != null, "Goal did not prioritize duplicates");
    CleanupPlanner.ValidateSelection(plan.Where(i => i.Selected).ToArray());
});
Test("Changed files are refused even when their filenames are unchanged", f =>
{
    string a = f.Write("Downloads/a.zip", 90000, 4, 100);
    var file = f.Scan().Index.EnumerateFiles().Single(); File.AppendAllText(a, "changed");
    Throws(() => FilePolicy.ValidateUnchanged(file), "Changed file accepted");
});
Test("Duplicate cleanup detects content changes with preserved size and timestamp", f =>
{
    string a = f.Write("Pictures/a.png", 131072, 7); f.Copy(a, "Downloads/b.png");
    var scan = f.Scan(); var dup = new DuplicateFinder().Find(scan.Index, null, default);
    var item = CleanupPlanner.Candidates(scan, DateTime.UtcNow).Items.Single();
    DateTime modified = File.GetLastWriteTimeUtc(item.Path);
    byte[] data = File.ReadAllBytes(item.Path); data[65000] ^= 255; File.WriteAllBytes(item.Path, data); File.SetLastWriteTimeUtc(item.Path, modified);
    Throws(() => CleanupPlanner.VerifyForCleanup(item, default), "Changed duplicate was accepted");
});
Test("Missing keeper blocks duplicate recycling", f =>
{
    string a = f.Write("Pictures/a.png", 80000, 1); f.Copy(a, "Downloads/b.png");
    var scan = f.Scan(); var dup = new DuplicateFinder().Find(scan.Index, null, default);
    var item = CleanupPlanner.Candidates(scan, DateTime.UtcNow).Items.Single();
    File.Delete(item.KeeperPath!);
    Throws(() => CleanupPlanner.VerifyForCleanup(item, default), "Missing keeper allowed");
});
Test("Cleanup rejects duplicate requests and protected paths", f =>
{
    f.Write("Downloads/a.zip", 80000, 1, 100);
    var item = CleanupPlanner.Candidates(f.Scan(), DateTime.UtcNow).Items.Single();
    Throws(() => CleanupPlanner.ValidateSelection([item, item]), "Double-counted selection accepted");
    Assert(!FilePolicy.IsPersonalFile(@"C:\Windows\a.pdf", FileAttributes.Normal), "Windows file allowed");
    Assert(!FilePolicy.IsPersonalFile(@"D:\SteamLibrary\steamapps\common\Game\movie.mp4", FileAttributes.Normal), "Game file allowed");
    Assert(!FilePolicy.IsPersonalFile(@"C:\Users\Me\a.dll", FileAttributes.Normal), "Application binary allowed");
});
Test("History compares the same root and preserves baseline on cancellation", f =>
{
    f.Write("Videos/a.mp4", 100, 1);
    var store = new HistoryStore(Path.Combine(f.Root, "history-store")); var before = f.Scan();
    store.Save(before); var saved = store.Read(f.Root)!;
    f.Write("Videos/b.mp4", 90, 2); var after = f.Scan();
    var changes = HistoryStore.Compare(saved, store.Capture(after));
    Assert(changes.Single(c => c.Path.EndsWith("Videos")).Delta == 90, "Wrong growth");
    after.Cancelled = true; store.Save(after);
    Assert(store.Read(f.Root)!.WhenUtc == saved.WhenUtc, "Cancelled scan overwrote baseline");
});
Test("CSV quotes paths, Unicode, and spreadsheet formula prefixes", f =>
{
    string path = Path.Combine(f.Root, "report.csv");
    CsvExport.Write(path, [new FileRecord("=DANGER,\"café\"", 12, DateTime.UtcNow.Ticks, FileAttributes.Normal, "Documents")]);
    string csv = File.ReadAllText(path);
    Assert(csv.Contains("\"'=DANGER,\"\"café\"\"\"") && csv.Contains(",12,"), "Unsafe or malformed CSV");
});
Test("Treemap conserves area, proportions, and never overlaps", f =>
{
    long[] weights = [1000, 700, 500, 40, 10, 1]; var boxes = TreemapLayout.Arrange(weights, 950, 470);
    double total = weights.Sum();
    for (int i = 0; i < boxes.Count; i++)
    {
        var r = boxes[i];
        Assert(r.Width >= 0 && r.Height >= 0 && r.X >= 0 && r.Y >= 0 && r.X + r.Width <= 950.0001 && r.Y + r.Height <= 470.0001, "Out-of-bounds box");
        Assert(Math.Abs(r.Width * r.Height / (950 * 470) - weights[i] / total) < 0.000001, "Incorrect proportional area");
        for (int j = i + 1; j < boxes.Count; j++)
        { var b = boxes[j]; double overlap = Math.Max(0, Math.Min(r.X + r.Width, b.X + b.Width) - Math.Max(r.X, b.X)) * Math.Max(0, Math.Min(r.Y + r.Height, b.Y + b.Height) - Math.Max(r.Y, b.Y)); Assert(overlap < 0.0001, "Overlapping rectangles"); }
    }
});
Test("Empty folders and zero-weight maps remain valid", f =>
{
    Assert(f.Scan().Root.Bytes == 0, "Empty folder failed");
    Assert(TreemapLayout.Arrange([], 100, 100).Count == 0, "Empty layout failed");
    Assert(TreemapLayout.Arrange([0, 0], 100, 100).All(r => r.Width == 0), "Zero weights failed");
});


Test("File, folder, search and sorted pages cover the inventory exactly", f =>
{
    for (int i = 0; i < 1507; i++) f.Write($"wide/file-{i:D4}.txt", i % 9 + 1, i);
    f.Write("wide/café_'%_文件.txt", 11, 1);
    var scan = f.Scan();
    var seen = new HashSet<string>();
    long previous = long.MaxValue;
    for (int offset = 0; offset < scan.FileCount; offset += FileIndex.PageSize)
    {
        var page = scan.Index.FilesPage(offset: offset);
        Assert(page.Items.Count <= 500 && page.TotalCount == 1508, "Page was unbounded or count was wrong");
        foreach (var file in page.Items) { Assert(file.Size <= previous && seen.Add(file.Path), "Sort or paging repeated a file"); previous = file.Size; }
    }
    Assert(seen.Count == 1508, "Lost files across pages");
    Assert(scan.Index.FilesPage(search: "CAFÉ_'%_").Items.Single().Name == "café_'%_文件.txt", "Literal/Unicode filter was wrong");
    Assert(scan.Index.FilesPage(category: "Photos").TotalCount == 0, "Category filter failed");
    Assert(scan.Index.FilesPage(sort: FileSort.Smallest).Items.First().Size == 1, "Global ascending sort failed");
    var wide = scan.Index.ChildFolders(1).Single();
    var folderPage = scan.Index.FolderPage(wide.Id, 1500);
    Assert(folderPage.Items.Count == 8 && folderPage.TotalCount == 1508, "Folder paging failed");
    var map = scan.Index.FolderPage(wide.Id, 0, 79);
    Assert(map.Items.Count == 79 && map.Items.Sum(i => i.Bytes) <= wide.Bytes, "Map page incorrect");
    string export = Path.Combine(f.Root, "all.csv"); CsvExport.Write(export, scan.Index.EnumerateFiles());
    Assert(File.ReadLines(export).LongCount() == 1509, "Streaming export was truncated to the page");
});
Test("Serial and parallel traversal agree on all folder totals", f =>
{
    for (int i = 0; i < 1200; i++) f.Write($"top-{i % 8}/nested-{i % 7}/f-{i}.txt", i % 11 + 1, i);
    using var one = new Scanner().Scan(f.Root, null, default, new(1));
    using var four = new Scanner().Scan(f.Root, null, default, new(4));
    Assert(one.FileCount == four.FileCount && one.Root.Bytes == four.Root.Bytes, "Parallel aggregate lost bytes");
    Assert(one.Index.HistoryFolders().ToDictionary(i => i.Path, i => i.Bytes).OrderBy(i => i.Key).SequenceEqual(four.Index.HistoryFolders().ToDictionary(i => i.Path, i => i.Bytes).OrderBy(i => i.Key)), "Nested folder totals differ");
});
Test("Cloud roots and cloud subtrees are rejected; developer files still count", f =>
{
    f.Write("normal/a.txt", 7, 1); f.Write("OneDrive - Personal/deep/a.txt", 90, 1);
    f.Write("normal/Dropbox/deep/a.txt", 80, 1); f.Write("normal/.git/objects/a.txt", 13, 1);
    var scan = f.Scan();
    Assert(scan.FileCount == 2 && scan.Root.Bytes == 20 && scan.Skipped == 2, "Cloud subtree descended or developer storage missing");
    Assert(scan.Index.EnumerateFiles().Single(i => i.Path.Contains(".git")).CanClean == false, "Developer file eligible for cleanup");
    Throws(() => new Scanner().Scan(Path.Combine(f.Root, "OneDrive - Personal/deep"), null, default), "Cloud ancestor root accepted");
});
Test("Linked ancestors are refused even when the selected root is below the link", f =>
{
    f.Write("real/deep/a.txt", 20, 1);
    Directory.CreateSymbolicLink(Path.Combine(f.Root, "link"), Path.Combine(f.Root, "real"));
    Throws(() => new Scanner().Scan(Path.Combine(f.Root, "link/deep"), null, default), "Linked ancestor accepted");
});
Test("Lazy folder enumeration failure ends that branch and preserves others", f =>
{
    f.Write("broken/a.txt", 19, 1); f.Write("healthy/b.txt", 23, 1);
    IEnumerable<FileSystemInfo> Fail(string path)
    {
        yield return new FileInfo(Path.Combine(path, "a.txt"));
        throw new UnauthorizedAccessException("simulated MoveNext failure");
    }
    var scanner = new Scanner { Enumerate = path => Path.GetFileName(path) == "broken" ? Fail(path).GetEnumerator() : new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator() };
    using var scan = scanner.Scan(f.Root, null, default);
    Assert(scan.FileCount == 2 && scan.Root.Bytes == 42 && scan.Skipped == 1, "Folder failure lost sibling or previously recorded bytes");
});
Test("Progress reports are count-batched and always include final totals", f =>
{
    for (int i = 0; i < 1205; i++) f.Write($"file-{i}.txt", 1, i);
    var reports = new List<ScanProgress>();
    using var scan = new Scanner().Scan(f.Root, new InlineProgress(reports.Add), default, new(1));
    Assert(reports.Where(p => p.Phase == "Scanning").Select(p => p.Files).SequenceEqual(new long[] { 500, 1000 }), "Progress was not count-batched");
    Assert(reports.Last().Files == 1205 && reports.Last().Bytes == 1205, "Final progress missing");
});
Test("Cancellation during parallel traversal drains frames and commits a consistent partial index", f =>
{
    for (int i = 0; i < 2500; i++) f.Write($"top-{i % 6}/nested/file-{i}.txt", 3, i);
    using var cancel = new CancellationTokenSource();
    using var scan = new Scanner().Scan(f.Root, new InlineProgress(p => { if (p.Phase == "Scanning" && p.Files >= 500) cancel.Cancel(); }), cancel.Token, new(4));
    var totals = scan.Index.FilesPage();
    Assert(scan.Cancelled && scan.FileCount >= 500 && scan.FileCount < 2500, "Cancellation did not stop work");
    Assert(scan.FileCount == totals.TotalCount && scan.Root.Bytes == totals.TotalBytes && scan.Root.FileCount == totals.TotalCount, "Partial ancestor totals lost entries");
    Assert(CleanupPlanner.Candidates(scan, DateTime.UtcNow).Items.Count == 0, "Partial scan offered cleanup");
});
Test("Duplicate groups larger than a page preserve exactly one keeper", f =>
{
    string source = f.Write("Pictures/keeper.png", 65536, 18, 100);
    for (int i = 0; i < 1002; i++) f.Copy(source, $"Downloads/copy-{i:D4}.png");
    var scan = f.Scan(); var result = new DuplicateFinder().Find(scan.Index, null, default);
    Assert(result.GroupCount == 1 && result.CopyCount == 1003 && result.ExtraBytes == 1002L * 65536, "Large duplicate group truncated");
    var rows = new List<DuplicateRow>();
    for (int offset = 0; offset < result.CopyCount; offset += 500) rows.AddRange(scan.Index.DuplicatePage(offset));
    Assert(rows.Count(r => r.Keeper) == 1 && rows.Single(r => r.Keeper).File.Path == source, "Keeper differs across pages");
    var first = CleanupPlanner.Candidates(scan, DateTime.UtcNow);
    var last = scan.Index.CleanupPage(1000);
    Assert(first.TotalCount == 1002 && first.Items.Count == 1000 && last.Items.Count == 2, "Cleanup pages lost candidates");
    Assert(first.Items.Concat(last.Items).All(i => i.Path != source && i.KeeperPath == source), "Keeper included or forgotten");
    var rerun = new DuplicateFinder().Find(scan.Index, null, default);
    Assert(rerun.CopyCount == 1003, "Repeated duplicate search accumulated stale results");
});
Test("Stopped duplicate search publishes no sample-only matches", f =>
{
    string source = f.Write("Pictures/a.png", 65536, 1); f.Copy(source, "Downloads/a.png");
    var scan = f.Scan(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    var result = new DuplicateFinder().Find(scan.Index, null, cancellation.Token);
    Assert(result.Cancelled && result.GroupCount == 0 && scan.Index.DuplicatePage().Count == 0, "Unverified matches published");
});
Test("The scan index does not scan itself and is deleted on disposal", f =>
{
    f.Write("source/a.txt", 12, 1);
    var scan = new Scanner().Scan(f.Root, null, default, new(2, Path.Combine(f.Root, "cache")));
    string database = scan.Index.DatabasePath;
    Assert(scan.FileCount == 1 && File.Exists(database), "Index included itself or was not on disk");
    scan.Dispose(); Assert(!Directory.Exists(Path.GetDirectoryName(database)), "Temporary inventory remained after disposal");
});
Test("History avoids materializing an unbounded shallow-folder inventory", f =>
{
    using var index = new FileIndex(); index.AddFolder(f.Root, 0, 0);
    for (int i = 0; i < 10001; i++) index.AddFolder(Path.Combine(f.Root, "folder-" + i), 1, 1);
    index.Flush(); using var scan = new ScanResult(index);
    var store = new HistoryStore(Path.Combine(f.Root, "history"));
    Throws(() => store.Capture(scan), "Unbounded history accepted");
});

if (args.Contains("--stress"))
{
    Test("One-million-record disk inventory stays bounded through paging and planning", f =>
    {
        using var index = new FileIndex(); long folder = index.AddFolder(Path.Combine(f.Root, "Downloads"), 0, 0);
        long baseline = GC.GetTotalMemory(true), peakManaged = baseline, peakRss = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long modified = DateTime.UtcNow.AddDays(-120).Ticks;
        for (int i = 0; i < 1_000_000; i++)
        {
            index.AddFile(folder, new(Path.Combine(f.Root, "Downloads", $"archive-{i:D7}.zip"), 1000 + i % 10000, modified, FileAttributes.Normal, "Archives"));
            if (i % 100000 == 0)
            {
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(true));
                peakRss = Math.Max(peakRss, System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
            }
        }
        index.Flush(); index.BuildIndexes();
        var first = index.FilesPage(); var last = index.FilesPage(offset: 999500);
        Assert(first.TotalCount == 1_000_000 && first.Items.Count == 500 && last.Items.Count == 500, "Million-record inventory lost data");
        index.StartDuplicates(); // Exercise size-group preparation without reading fake files.
        index.BuildCleanup(DateTime.UtcNow);
        Assert(index.CleanupPage().TotalCount == 1_000_000 && index.CleanupPage().Items.Count == 1000, "Planner hydrated/truncated inventory");
        peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(true));
        peakRss = Math.Max(peakRss, System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
        long diskBytes = Directory.EnumerateFiles(index.DirectoryPath).Sum(path => new FileInfo(path).Length);
        Console.WriteLine($"STRESS records=1000000 elapsed_seconds={timer.Elapsed.TotalSeconds:F2} retained_managed_delta_bytes={peakManaged - baseline} observed_peak_rss_bytes={peakRss} index_disk_bytes={diskBytes}");
        Assert(peakManaged - baseline < 64L * 1024 * 1024, "Retained managed memory grew with inventory");
    });
}

Console.WriteLine($"\n{passed} passed; {failed} failed. Native WPF and Windows Shell recycling require Windows validation.");
return failed == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sx-test-" + Guid.NewGuid().ToString("N"));
    public Fixture() => Directory.CreateDirectory(Root);
    public string Write(string relative, int size, int seed, int daysOld = 0)
    {
        string path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size]; new Random(seed).NextBytes(bytes); File.WriteAllBytes(path, bytes);
        if (daysOld > 0) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }
    public string Copy(string from, string relative)
    { string path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(from, path); return path; }
    public List<ScanResult> Scans { get; } = [];
    public ScanResult? LastScan => Scans.LastOrDefault();
    public ScanResult Scan() { var scan = new Scanner().Scan(Root, null, default); Scans.Add(scan); return scan; }
    public void Dispose() { foreach (var scan in Scans) scan.Dispose(); try { Directory.Delete(Root, true); } catch { } }
}

sealed class InlineProgress(Action<ScanProgress> action) : IProgress<ScanProgress>
{ public void Report(ScanProgress value) => action(value); }
