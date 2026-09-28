using System.Reflection;
using System.Runtime.InteropServices;

namespace StorageXray.Core;

// Small, parameterized wrapper over the OS SQLite engine. No user-provided SQL.
// Windows loads only the system copy; Linux is used by the filesystem test runner.
internal static class SqliteNative
{
    private const string Library = "StorageXray.Sqlite";
    static SqliteNative() => NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? search) => name != Library ? IntPtr.Zero :
        NativeLibrary.Load(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "winsqlite3.dll") : "libsqlite3.so.0");
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr context, IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int size, out IntPtr statement, IntPtr tail);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_reset(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_clear_bindings(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int size, IntPtr destructor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_last_insert_rowid(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Progress(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_progress_handler(IntPtr db, int operations, Progress? callback, IntPtr context);
}

// Intentionally NOT IOException: an index write failure must abort the scan, not
// be mistaken for an unreadable source folder and quietly produce incomplete data.
public sealed class IndexStorageException(string message) : Exception(message);

internal sealed class SqliteDb : IDisposable
{
    private IntPtr handle;
    private readonly CancellationToken cancellation;
    private readonly SqliteNative.Progress? progress;
    public SqliteDb(string path, bool readOnly = false, CancellationToken cancellation = default)
    {
        this.cancellation = cancellation;
        int status = SqliteNative.sqlite3_open_v2(path, out handle, (readOnly ? 1 : 2 | 4) | 0x10000, IntPtr.Zero);
        try
        {
            Check(status);
            Check(SqliteNative.sqlite3_busy_timeout(handle, 5000));
            if (cancellation.CanBeCanceled)
            {
                progress = _ => cancellation.IsCancellationRequested ? 1 : 0;
                SqliteNative.sqlite3_progress_handler(handle, 2000, progress, IntPtr.Zero);
            }
            Exec("PRAGMA cache_size=-4096; PRAGMA temp_store=FILE; PRAGMA mmap_size=0;");
        }
        catch { Dispose(); throw; }
    }
    internal void Check(int status)
    {
        if (status == 0) return;
        if (status == 9 && cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
        throw new IndexStorageException("Local scan index: " + (Marshal.PtrToStringUTF8(SqliteNative.sqlite3_errmsg(handle)) ?? status.ToString()));
    }
    public void Exec(string sql) => Check(SqliteNative.sqlite3_exec(handle, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    public SqliteStatement Prepare(string sql)
    {
        Check(SqliteNative.sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero));
        return new(this, statement);
    }
    public long LastId => SqliteNative.sqlite3_last_insert_rowid(handle);
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        SqliteNative.sqlite3_close_v2(handle); handle = IntPtr.Zero;
        GC.KeepAlive(progress);
    }
}

internal sealed class SqliteStatement(SqliteDb db, IntPtr handle) : IDisposable
{
    public SqliteStatement Bind(int position, long value) { db.Check(SqliteNative.sqlite3_bind_int64(handle, position, value)); return this; }
    public SqliteStatement Bind(int position, string value) { db.Check(SqliteNative.sqlite3_bind_text(handle, position, value, -1, new IntPtr(-1))); return this; }
    public bool Step()
    {
        int status = SqliteNative.sqlite3_step(handle);
        if (status == 100) return true;
        if (status == 101) return false;
        db.Check(status); return false;
    }
    public long Long(int column) => SqliteNative.sqlite3_column_int64(handle, column);
    public string Text(int column) => Marshal.PtrToStringUTF8(SqliteNative.sqlite3_column_text(handle, column)) ?? "";
    public void Run() { while (Step()) { } Reset(); }
    public void Reset() { db.Check(SqliteNative.sqlite3_reset(handle)); db.Check(SqliteNative.sqlite3_clear_bindings(handle)); }
    public void Dispose() { if (handle != IntPtr.Zero) { SqliteNative.sqlite3_finalize(handle); handle = IntPtr.Zero; } }
}
