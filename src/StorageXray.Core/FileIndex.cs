namespace StorageXray.Core;

// Scan inventory, sorting, grouping, and duplicate state all live on disk.
// Only a page of records is hydrated at any one time. The single writer is
// synchronized; filesystem workers remain independent of each other's IO.
public sealed class FileIndex : IDisposable
{
    public const int PageSize = 500;
    public const int CleanupPageSize = 1000;
    public static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorageXray", "scans");
    public string DirectoryPath { get; }
    public string DatabasePath => Path.Combine(DirectoryPath, "index.sqlite");
    private readonly object gate = new();
    private readonly SqliteDb writer;
    private readonly SqliteStatement insertFile, insertFolder, finishFolder, fingerprint;
    private bool disposed, transaction;
    private int writes;
    private const string FileColumns = "f.Path,f.Bytes,f.Modified,f.Attributes,f.Category,f.Id";

    public FileIndex(string? cacheDirectory = null)
    {
        DirectoryPath = Path.Combine(cacheDirectory ?? CacheRoot, "scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        writer = new(DatabasePath);
        try
        {
            writer.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA wal_autocheckpoint=512; PRAGMA journal_size_limit=4194304;");
            writer.Exec("""
                CREATE TABLE Folders(Id INTEGER PRIMARY KEY, Parent INTEGER NOT NULL, Path TEXT NOT NULL, Depth INTEGER NOT NULL, Bytes INTEGER NOT NULL DEFAULT 0, Files INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX FolderParent ON Folders(Parent,Bytes DESC,Id);
                CREATE TABLE Files(Id INTEGER PRIMARY KEY, Folder INTEGER NOT NULL, Path TEXT NOT NULL, SearchPath TEXT NOT NULL, Name TEXT NOT NULL, Bytes INTEGER NOT NULL, Modified INTEGER NOT NULL, Attributes INTEGER NOT NULL, Category TEXT NOT NULL, Eligible INTEGER NOT NULL, Download INTEGER NOT NULL);
                CREATE TABLE Fingerprints(FileId INTEGER PRIMARY KEY, Bytes INTEGER NOT NULL, Sample TEXT NOT NULL, Hash TEXT);
                CREATE TABLE DuplicateGroups(Id INTEGER PRIMARY KEY, Hash TEXT NOT NULL, Bytes INTEGER NOT NULL, Keeper INTEGER NOT NULL, Copies INTEGER NOT NULL, ExtraBytes INTEGER NOT NULL);
                CREATE TABLE Cleanup(FileId INTEGER PRIMARY KEY, Keeper INTEGER, Hash TEXT, Reason TEXT NOT NULL, Priority INTEGER NOT NULL);
                """);
            insertFile = writer.Prepare("INSERT INTO Files(Folder,Path,SearchPath,Name,Bytes,Modified,Attributes,Category,Eligible,Download) VALUES(?,?,?,?,?,?,?,?,?,?)");
            insertFolder = writer.Prepare("INSERT INTO Folders(Parent,Path,Depth) VALUES(?,?,?)");
            finishFolder = writer.Prepare("UPDATE Folders SET Bytes=?,Files=? WHERE Id=?");
            fingerprint = writer.Prepare("INSERT OR REPLACE INTO Fingerprints(FileId,Bytes,Sample,Hash) VALUES(?,?,?,NULL)");
        }
        catch { writer.Dispose(); try { Directory.Delete(DirectoryPath, true); } catch { } throw; }
    }
    private void Begin()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!transaction) { writer.Exec("BEGIN"); transaction = true; }
    }
    private void Written() { if (++writes >= 1000) Flush(); }
    public void Flush()
    {
        lock (gate)
        {
            if (transaction) { writer.Exec("COMMIT"); transaction = false; }
            writes = 0;
        }
    }
    public long AddFolder(string path, long parent, int depth)
    {
        lock (gate)
        {
            Begin(); insertFolder.Bind(1, parent).Bind(2, path).Bind(3, depth).Run();
            long id = writer.LastId; Written(); return id;
        }
    }
    public void AddFile(long folder, FileRecord file)
    {
        lock (gate)
        {
            Begin();
            insertFile.Bind(1, folder).Bind(2, file.Path).Bind(3, file.Path.ToUpperInvariant()).Bind(4, file.Name)
                .Bind(5, file.Size).Bind(6, file.ModifiedTicks).Bind(7, (long)file.Attributes).Bind(8, file.Category)
                .Bind(9, file.CanClean && !FilePolicy.IsCloudPath(file.Path) ? 1 : 0)
                .Bind(10, FilePolicy.Segments(file.Path).Contains("Downloads", StringComparer.OrdinalIgnoreCase) ? 1 : 0).Run();
            Written();
        }
    }
    public void FinishFolder(long id, long bytes, long files)
    {
        lock (gate) { Begin(); finishFolder.Bind(1, bytes).Bind(2, files).Bind(3, id).Run(); Written(); }
    }
    public void CompleteRoot(long directBytes, long directFiles)
    {
        lock (gate)
        {
            Flush();
            using var q = writer.Prepare("SELECT COALESCE(SUM(Bytes),0),COALESCE(SUM(Files),0) FROM Folders WHERE Parent=1");
            q.Step(); FinishFolder(1, directBytes + q.Long(0), directFiles + q.Long(1)); Flush();
        }
    }
    public void BuildIndexes()
    {
        lock (gate)
        {
            Flush();
            writer.Exec("""
                CREATE INDEX IF NOT EXISTS FileSize ON Files(Bytes DESC,Id);
                CREATE INDEX IF NOT EXISTS FileFolder ON Files(Folder,Bytes DESC,Id);
                CREATE INDEX IF NOT EXISTS FileEligible ON Files(Bytes,Id) WHERE Eligible=1;
                CREATE INDEX IF NOT EXISTS FileModified ON Files(Modified DESC,Id);
                CREATE INDEX IF NOT EXISTS FileName ON Files(Name COLLATE NOCASE,Id);
                CREATE INDEX IF NOT EXISTS PrintSample ON Fingerprints(Bytes,Sample,FileId);
                CREATE INDEX IF NOT EXISTS PrintHash ON Fingerprints(Bytes,Hash,FileId);
                CREATE INDEX IF NOT EXISTS GroupHash ON DuplicateGroups(Bytes,Hash);
                """);
        }
    }
    private SqliteDb Read(CancellationToken cancellation = default)
    { ObjectDisposedException.ThrowIf(disposed, this); return new(DatabasePath, true, cancellation); }
    private static FileRecord File(SqliteStatement q, int start = 0) => new(q.Text(start), q.Long(start + 1), q.Long(start + 2), (FileAttributes)q.Long(start + 3), q.Text(start + 4)) { Id = q.Long(start + 5) };
    private static FolderNode Folder(SqliteStatement q) => new(q.Long(0), q.Long(1), q.Text(2), q.Long(3), q.Long(4));
    public FolderNode GetFolder(long id)
    {
        using var db = Read(); using var q = db.Prepare("SELECT Id,Parent,Path,Bytes,Files FROM Folders WHERE Id=?").Bind(1, id);
        if (!q.Step()) throw new IOException("This folder is not in the scan."); return Folder(q);
    }
    public IEnumerable<FolderNode> ChildFolders(long parent)
    {
        using var db = Read(); using var q = db.Prepare("SELECT Id,Parent,Path,Bytes,Files FROM Folders WHERE Parent=? ORDER BY Id").Bind(1, parent);
        while (q.Step()) yield return Folder(q);
    }
    public long FolderCount()
    { using var db = Read(); using var q = db.Prepare("SELECT COUNT(*) FROM Folders"); q.Step(); return q.Long(0); }
    internal IEnumerable<FolderNode> TopLevelFolders()
    {
        long lastId = 0;
        while (true)
        {
            var batch = new List<FolderNode>(64);
            using (var db = Read())
            using (var q = db.Prepare("SELECT Id,Parent,Path,Bytes,Files FROM Folders WHERE Parent=1 AND Id>? ORDER BY Id LIMIT 64").Bind(1, lastId))
                while (q.Step()) batch.Add(Folder(q));
            if (batch.Count == 0) yield break;
            foreach (var folder in batch) { lastId = folder.Id; yield return folder; }
        }
    }
    public FilePage FilesPage(string search = "", string? category = null, FileSort sort = FileSort.Largest, long offset = 0, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var db = Read(cancellation);
        string filter = " WHERE (?='' OR instr(f.SearchPath,?)>0) AND (?='' OR f.Category=?)";
        string order = sort switch { FileSort.Smallest => "f.Bytes ASC,f.Id", FileSort.Newest => "f.Modified DESC,f.Id", FileSort.Oldest => "f.Modified ASC,f.Id", FileSort.Name => "f.Name COLLATE NOCASE,f.Id", _ => "f.Bytes DESC,f.Id" };
        SqliteStatement Bind(SqliteStatement q) => q.Bind(1, search.ToUpperInvariant()).Bind(2, search.ToUpperInvariant()).Bind(3, category ?? "").Bind(4, category ?? "");
        long count, bytes;
        using (var stats = Bind(db.Prepare("SELECT COUNT(*),COALESCE(SUM(f.Bytes),0) FROM Files f" + filter)))
        { stats.Step(); count = stats.Long(0); bytes = stats.Long(1); }
        using var page = Bind(db.Prepare("SELECT " + FileColumns + " FROM Files f" + filter + " ORDER BY " + order + " LIMIT ? OFFSET ?")).Bind(5, PageSize).Bind(6, offset);
        var files = new List<FileRecord>(PageSize); while (page.Step()) files.Add(File(page));
        return new(files, count, bytes);
    }
    public IEnumerable<FileRecord> EnumerateFiles(CancellationToken cancellation = default)
    {
        using var db = Read(cancellation); using var q = db.Prepare("SELECT " + FileColumns + " FROM Files f ORDER BY f.Bytes DESC,f.Id");
        while (q.Step()) { cancellation.ThrowIfCancellationRequested(); yield return File(q); }
    }
    public FolderPage FolderPage(long id, long offset = 0, int limit = PageSize)
    {
        if (offset < 0 || limit < 1 || limit > PageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        var folder = GetFolder(id); using var db = Read();
        using var count = db.Prepare("SELECT (SELECT COUNT(*) FROM Folders WHERE Parent=?)+(SELECT COUNT(*) FROM Files WHERE Folder=?)").Bind(1, id).Bind(2, id);
        count.Step(); long total = count.Long(0);
        using var q = db.Prepare("""
            SELECT Id,Parent,Path,Bytes,Files,0,0,'Folder',1 FROM Folders WHERE Parent=?
            UNION ALL SELECT Id,Folder,Path,Bytes,0,Modified,Attributes,Category,0 FROM Files WHERE Folder=?
            ORDER BY 4 DESC,3 LIMIT ? OFFSET ?
            """).Bind(1, id).Bind(2, id).Bind(3, limit).Bind(4, offset);
        var items = new List<FolderEntry>(limit);
        while (q.Step())
        {
            if (q.Long(8) == 1)
            { var child = Folder(q); items.Add(new(child.Name, child.Path, child.Bytes, "Folder", child, null)); }
            else
            {
                var file = new FileRecord(q.Text(2), q.Long(3), q.Long(5), (FileAttributes)q.Long(6), q.Text(7)) { Id = q.Long(0) };
                items.Add(new(file.Name, file.Path, file.Size, file.Category, null, file));
            }
        }
        return new(folder, items, total);
    }
    public IEnumerable<(string Path, long Bytes)> HistoryFolders()
    {
        using var db = Read(); using var q = db.Prepare("SELECT Path,Bytes FROM Folders WHERE Depth BETWEEN 1 AND 3 ORDER BY Path LIMIT 10001");
        while (q.Step()) yield return (q.Text(0), q.Long(1));
    }
    public void StartDuplicates()
    {
        lock (gate)
        {
            Flush(); writer.Exec("DELETE FROM Cleanup; DELETE FROM Fingerprints; DELETE FROM DuplicateGroups; DROP TABLE IF EXISTS DuplicateSizes; DROP TABLE IF EXISTS RepeatedSamples;");
            writer.Exec($"CREATE TABLE DuplicateSizes(Bytes INTEGER PRIMARY KEY); INSERT INTO DuplicateSizes SELECT Bytes FROM Files WHERE Eligible=1 AND Bytes>={DuplicateFinder.MinimumBytes} GROUP BY Bytes HAVING COUNT(*)>1;");
        }
    }
    public IReadOnlyList<FileRecord> DuplicateCandidates(long afterId, bool fullHash)
    {
        using var db = Read();
        string from = fullHash ? " FROM Files f JOIN Fingerprints p ON p.FileId=f.Id JOIN RepeatedSamples r ON r.Bytes=p.Bytes AND r.Sample=p.Sample" : " FROM Files f JOIN DuplicateSizes d ON d.Bytes=f.Bytes";
        using var q = db.Prepare("SELECT " + FileColumns + from + " WHERE f.Eligible=1 AND f.Id>? ORDER BY f.Id LIMIT ?").Bind(1, afterId).Bind(2, PageSize);
        var files = new List<FileRecord>(PageSize); while (q.Step()) files.Add(File(q)); return files;
    }
    public void SaveSample(FileRecord file, string sample)
    { lock (gate) { Begin(); fingerprint.Bind(1, file.Id).Bind(2, file.Size).Bind(3, sample).Run(); Written(); } }
    public void PrepareFullHashes()
    {
        lock (gate)
        {
            Flush(); writer.Exec("CREATE TABLE RepeatedSamples(Bytes INTEGER NOT NULL,Sample TEXT NOT NULL,PRIMARY KEY(Bytes,Sample)); INSERT INTO RepeatedSamples SELECT Bytes,Sample FROM Fingerprints GROUP BY Bytes,Sample HAVING COUNT(*)>1;");
        }
    }
    public void SaveHash(long id, string hash)
    {
        lock (gate)
        {
            Begin(); using var q = writer.Prepare("UPDATE Fingerprints SET Hash=? WHERE FileId=?").Bind(1, hash).Bind(2, id); q.Run(); Written();
        }
    }
    public DuplicateResult FinishDuplicates(long skipped, bool cancelled)
    {
        lock (gate)
        {
            Flush();
            writer.Exec("""
                INSERT INTO DuplicateGroups(Hash,Bytes,Keeper,Copies,ExtraBytes)
                SELECT p.Hash,p.Bytes,
                    (SELECT f.Id FROM Files f JOIN Fingerprints k ON k.FileId=f.Id
                     WHERE k.Bytes=p.Bytes AND k.Hash=p.Hash ORDER BY f.Download,f.Modified,f.Path COLLATE NOCASE,f.Id LIMIT 1),
                    COUNT(*),(COUNT(*)-1)*p.Bytes
                FROM Fingerprints p WHERE p.Hash IS NOT NULL GROUP BY p.Bytes,p.Hash HAVING COUNT(*)>1;
                """);
        }
        using var db = Read(); using var stats = db.Prepare("SELECT COUNT(*),COALESCE(SUM(Copies),0),COALESCE(SUM(ExtraBytes),0) FROM DuplicateGroups");
        stats.Step(); return new(stats.Long(0), stats.Long(1), stats.Long(2), skipped, cancelled);
    }
    public IReadOnlyList<DuplicateRow> DuplicatePage(long offset = 0)
    {
        using var db = Read();
        using var q = db.Prepare("SELECT " + FileColumns + ",g.Id,f.Id=g.Keeper,g.Hash,k.Path FROM DuplicateGroups g JOIN Fingerprints p ON p.Bytes=g.Bytes AND p.Hash=g.Hash JOIN Files f ON f.Id=p.FileId JOIN Files k ON k.Id=g.Keeper ORDER BY g.ExtraBytes DESC,g.Id,(f.Id=g.Keeper) DESC,f.Path LIMIT ? OFFSET ?").Bind(1, PageSize).Bind(2, offset);
        var rows = new List<DuplicateRow>(PageSize); while (q.Step()) rows.Add(new(q.Long(6), q.Long(7) != 0, File(q), q.Text(8), q.Text(9))); return rows;
    }
    public void BuildCleanup(DateTime now)
    {
        lock (gate)
        {
            Flush(); writer.Exec("DELETE FROM Cleanup;");
            writer.Exec("""
                INSERT INTO Cleanup(FileId,Keeper,Hash,Reason,Priority)
                SELECT p.FileId,g.Keeper,g.Hash,'Verified duplicate · another copy kept',0
                FROM DuplicateGroups g JOIN Fingerprints p ON p.Bytes=g.Bytes AND p.Hash=g.Hash WHERE p.FileId<>g.Keeper;
                """);
            using var q = writer.Prepare("""
                INSERT INTO Cleanup(FileId,Reason,Priority)
                SELECT f.Id,CASE WHEN f.Category='Archives' THEN 'Download archive · unchanged for 60+ days' ELSE 'Large video · unchanged for 90+ days' END,1
                FROM Files f WHERE f.Eligible=1 AND
                    ((f.Category='Archives' AND f.Download=1 AND f.Modified<=?) OR (f.Category='Videos' AND f.Bytes>=104857600 AND f.Modified<=?))
                    AND NOT EXISTS(SELECT 1 FROM Fingerprints p JOIN DuplicateGroups g ON g.Bytes=p.Bytes AND g.Hash=p.Hash WHERE p.FileId=f.Id)
                """).Bind(1, now.AddDays(-60).Ticks).Bind(2, now.AddDays(-90).Ticks); q.Run();
        }
    }
    public CleanupPage CleanupPage(long offset = 0)
    {
        using var db = Read(); using var count = db.Prepare("SELECT COUNT(*) FROM Cleanup"); count.Step(); long total = count.Long(0);
        using var q = db.Prepare("SELECT " + FileColumns + ",c.Reason,k.Path,c.Hash FROM Cleanup c JOIN Files f ON f.Id=c.FileId LEFT JOIN Files k ON k.Id=c.Keeper ORDER BY c.Priority,f.Bytes DESC,f.Id LIMIT ? OFFSET ?").Bind(1, CleanupPageSize).Bind(2, offset);
        var items = new List<CleanupItem>(CleanupPageSize);
        while (q.Step()) items.Add(new() { File = File(q), Reason = q.Text(6), KeeperPath = q.Text(7) is { Length: > 0 } keep ? keep : null, ExpectedHash = q.Text(8) is { Length: > 0 } hash ? hash : null });
        return new(items, total);
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            insertFile.Dispose(); insertFolder.Dispose(); finishFolder.Dispose(); fingerprint.Dispose(); writer.Dispose();
            try { Directory.Delete(DirectoryPath, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
