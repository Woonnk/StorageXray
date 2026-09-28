namespace StorageXray.Core;

public static class CleanupPlanner
{
    public static CleanupPage Candidates(ScanResult scan, DateTime now, long offset = 0)
    {
        if (scan.Cancelled) return new([], 0);
        scan.Index.BuildCleanup(now);
        return scan.Index.CleanupPage(offset);
    }
    public static long SelectGoal(IList<CleanupItem> items, long goal)
    {
        if (goal <= 0) throw new ArgumentOutOfRangeException(nameof(goal));
        long total = 0;
        foreach (var item in items) item.Selected = false;
        foreach (var item in items.OrderBy(i => i.ExpectedHash == null ? 1 : 0).ThenByDescending(i => i.File.Size))
        {
            if (total >= goal) break;
            item.Selected = true; total += item.File.Size;
        }
        return total;
    }
    public static void ValidateSelection(IReadOnlyList<CleanupItem> selected)
    {
        var paths = new HashSet<string>(selected.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        if (paths.Count != selected.Count) throw new IOException("The plan contains a file more than once.");
        foreach (var item in selected)
        {
            FilePolicy.ValidateUnchanged(item.File);
            if (item.KeeperPath != null && paths.Contains(item.KeeperPath))
                throw new IOException("A duplicate's kept copy is also selected. Rebuild the plan.");
        }
    }
    public static void VerifyForCleanup(CleanupItem item, CancellationToken cancellation)
    {
        using var lease = AcquireCleanupLease(item, cancellation);
    }
    public static CleanupLease AcquireCleanupLease(CleanupItem item, CancellationToken cancellation)
    {
        FileStream? source = null, keeper = null;
        try
        {
            FilePolicy.ValidateUnchanged(item.File);
            // Block writers while allowing Windows to move the selected file to the Recycle Bin.
            source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!FileIdentity.IsSingleLink(source)) throw new IOException("Hard-linked files cannot be cleaned here.");
            if (item.ExpectedHash != null && DuplicateFinder.Hash(source, cancellation) != item.ExpectedHash)
                throw new IOException("Duplicate content changed; rescan before cleaning.");
            if (item.KeeperPath != null)
            {
                FilePolicy.CheckAncestors(item.KeeperPath);
                // Keep this handle open through recycling. No writer, rename, or deletion is permitted.
                keeper = DuplicateFinder.OpenStable(item.KeeperPath);
                if (keeper.Length != item.File.Size || DuplicateFinder.Hash(keeper, cancellation) != item.ExpectedHash)
                    throw new IOException("The kept copy changed or is missing. This duplicate was not removed.");
            }
            FilePolicy.ValidateUnchanged(item.File);
            return new(source, keeper);
        }
        catch { source?.Dispose(); keeper?.Dispose(); throw; }
    }
}

public sealed class CleanupLease(FileStream source, FileStream? keeper) : IDisposable
{
    public void Dispose() { source.Dispose(); keeper?.Dispose(); }
}
