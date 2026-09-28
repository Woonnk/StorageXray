using System.Runtime.InteropServices;
using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace StorageXray.Core;

public static class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    public static bool IsSingleLink(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) return true;
        return GetFileInformationByHandle(stream.SafeFileHandle, out var info) && info.Links == 1;
    }
}

public sealed class DuplicateFinder
{
    public const long MinimumBytes = 64 * 1024;
    public DuplicateResult Find(FileIndex index, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        index.StartDuplicates();
        long skipped = 0, checkedFiles = 0, readBytes = 0;
        bool cancelled = false;
        string current = "";
        try
        {
            for (int phase = 0; phase < 2; phase++)
            {
                if (phase == 1) index.PrepareFullHashes();
                long afterId = 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var page = index.DuplicateCandidates(afterId, phase == 1);
                    if (page.Count == 0) break;
                    foreach (var file in page)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        afterId = file.Id; current = file.Path;
                        string? value = null;
                        try
                        {
                            FilePolicy.ValidateUnchanged(file);
                            using var stream = OpenStable(file.Path);
                            if (!FileIdentity.IsSingleLink(stream)) skipped++;
                            else
                            {
                                value = phase == 0 ? Sample(stream) : Hash(stream, cancellation);
                                FilePolicy.ValidateUnchanged(file);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        { skipped++; value = null; }
                        // Keep index failures outside source-file error handling.
                        if (value != null)
                        {
                            if (phase == 0) index.SaveSample(file, value);
                            else { index.SaveHash(file.Id, value); readBytes += file.Size; }
                        }
                        checkedFiles++;
                        if (checkedFiles % 500 == 0)
                            progress?.Report(new(checkedFiles, readBytes, current, phase == 0 ? "Checking duplicate samples" : "Verifying full file contents"));
                    }
                    index.Flush();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
        var result = index.FinishDuplicates(skipped, cancelled);
        progress?.Report(new(checkedFiles, readBytes, current, cancelled ? "Duplicate search stopped" : "Duplicate search complete"));
        return result;
    }
    public static FileStream OpenStable(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    public static string Hash(Stream stream, CancellationToken cancellation)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            { cancellation.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, count); }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    private static string Sample(FileStream stream)
    {
        var buffer = new byte[8192];
        stream.ReadExactly(buffer.AsSpan(0, 4096));
        stream.Seek(stream.Length - 4096, SeekOrigin.Begin);
        stream.ReadExactly(buffer.AsSpan(4096, 4096));
        return Convert.ToHexString(SHA256.HashData(buffer));
    }
}
