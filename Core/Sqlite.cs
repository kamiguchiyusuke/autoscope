using System.Runtime.InteropServices;
namespace ArmadaAnalyzer;

internal sealed class Db : IDisposable
{
    internal IntPtr Handle;
    // Each connection belongs to one scoped worker; connections are never shared across threads.
    internal Db(string path)
    {
        int rc = Native.sqlite3_open_v2(path, out Handle, 0x2 | 0x4 | 0x8000, IntPtr.Zero);
        if (rc != 0)
        {
            string e = Error();
            Native.sqlite3_close(Handle);
            Handle = IntPtr.Zero;
            throw new IOException(e);
        }
        Native.sqlite3_busy_timeout(Handle, 5000);
        Exec("PRAGMA foreign_keys=ON; PRAGMA cache_size=-16384; PRAGMA synchronous=NORMAL;");
    }
    internal string Error() => Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(Handle)) ?? "SQLite error";
    internal void Exec(string sql)
    {
        int rc = Native.sqlite3_exec(Handle, sql, IntPtr.Zero, IntPtr.Zero, out var e);
        string? err = Marshal.PtrToStringUTF8(e);
        if (e != IntPtr.Zero)
            Native.sqlite3_free(e);
        if (rc != 0)
            throw new IOException(err ?? Error());
    }
    internal Statement Prepare(string sql) => new(this, sql);
    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.sqlite3_close(Handle);
            Handle = IntPtr.Zero;
        }
    }
    internal sealed class Statement : IDisposable
    {
        readonly Db db; IntPtr p;
        internal Statement(Db db, string sql)
        {
            this.db = db;
            if (Native.sqlite3_prepare_v2(db.Handle, sql, -1, out p, IntPtr.Zero) != 0)
                throw new IOException(db.Error());
        }
        internal Statement Bind(int index, long value)
        {
            Check(Native.sqlite3_bind_int64(p, index, value));
            return this;
        }
        internal Statement Bind(int index, string value)
        {
            Check(Native.sqlite3_bind_text(p, index, value, -1, new IntPtr(-1)));
            return this;
        }
        internal bool Step()
        {
            int rc = Native.sqlite3_step(p);
            if (rc == 100)
                return true;
            if (rc == 101)
                return false;
            throw new IOException(db.Error());
        }
        internal long Long(int col) => Native.sqlite3_column_int64(p, col);
        internal string Text(int col) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(p, col)) ?? "";
        internal void Reset()
        {
            Check(Native.sqlite3_reset(p));
            Check(Native.sqlite3_clear_bindings(p));
        }
        void Check(int rc)
        {
            if (rc != 0)
                throw new IOException(db.Error());
        }
        public void Dispose()
        {
            if (p != IntPtr.Zero)
            {
                Native.sqlite3_finalize(p);
                p = IntPtr.Zero;
            }
        }
    }
    internal static class Native
    {
        const string Lib = "ArmadaSqlite";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr cb, IntPtr arg, out IntPtr err);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr p);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr stmt, int i, long val);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr stmt, int i, [MarshalAs(UnmanagedType.LPUTF8Str)] string val, int bytes, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_reset(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_clear_bindings(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    }
}
