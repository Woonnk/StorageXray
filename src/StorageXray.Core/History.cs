using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StorageXray.Core;

public sealed record Snapshot(string Root, DateTime WhenUtc, long Bytes, long Files, Dictionary<string, long> Folders);
public sealed class HistoryStore(string directory)
{
    private string Location(string root) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24] + ".json");
    public Snapshot? Read(string root)
    {
        var path = Location(root);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("The saved history is too large to load safely. Remove that history baseline to start a new comparison.");
        return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
    }
    public Snapshot Capture(ScanResult scan)
    {
        var folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scan.Index.HistoryFolders())
        {
            if (folders.Count == 10000) throw new IOException("History is limited to 10,000 folders within the first three levels. This scan remains available, but the saved baseline was not changed.");
            folders[item.Path] = item.Bytes;
        }
        return new(scan.Root.Path, scan.CompletedUtc, scan.Root.Bytes, scan.FileCount, folders);
    }
    public void Save(ScanResult scan)
    {
        // Cancelled scans must not overwrite the saved baseline.
        if (scan.Cancelled) return;
        Directory.CreateDirectory(directory);
        var path = Location(scan.Root.Path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Capture(scan)));
        File.Move(temporary, path, true);
    }
    public static IReadOnlyList<FolderChange> Compare(Snapshot before, Snapshot after)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(before.Root), Path.GetFullPath(after.Root)))
            throw new ArgumentException("Snapshots must cover the same folder.");
        return before.Folders.Keys.Union(after.Folders.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(p => new FolderChange(p, before.Folders.GetValueOrDefault(p), after.Folders.GetValueOrDefault(p)))
            .Where(c => c.Delta != 0).OrderByDescending(c => Math.Abs(c.Delta)).ToArray();
    }
}

public static class CsvExport
{
    public static void Write(string path, IEnumerable<FileRecord> files)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Path,Bytes,Category,ModifiedUTC");
        foreach (var file in files) writer.WriteLine(string.Join(",", Cell(file.Path), file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), Cell(file.Category), Cell(new DateTime(file.ModifiedTicks, DateTimeKind.Utc).ToString("O"))));
    }
    private static string Cell(string value)
    {
        // Prevent spreadsheet software from interpreting filenames as formulas.
        if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
