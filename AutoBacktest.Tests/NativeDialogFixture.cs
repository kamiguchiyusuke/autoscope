using System.Runtime.InteropServices;
using System.Text;

internal sealed class NativeDialogFixture : IDisposable
{
    private delegate nint DialogProc(nint hwnd, uint message, nuint wparam, nint lparam);
    private readonly DialogProc callback;
    public nint Handle { get; }
    public bool Cancelled { get; private set; }
    public int CancelCount { get; private set; }
    public NativeDialogFixture(string title, int cancelId = 2, bool loginLabels = false, bool hideFirstCancellation = false, bool hiddenLabels = false)
    {
        callback = (hwnd, message, wparam, lparam) =>
        {
            if (message == 0x111 && (wparam & 0xffff) == (nuint)cancelId)
            { Cancelled = true; CancelCount++; if (hideFirstCancellation && CancelCount == 1) ShowWindow(hwnd, 0); else DestroyWindow(hwnd); return 1; }
            return 0;
        };
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.Unicode, true);
        void Text(string text) { writer.Write(Encoding.Unicode.GetBytes(text)); writer.Write((ushort)0); }
        writer.Write(0x90C80080u); writer.Write(0u); writer.Write((ushort)(loginLabels ? 5 : 1));
        writer.Write((short)-10000); writer.Write((short)-10000); writer.Write((short)160); writer.Write((short)70);
        writer.Write((ushort)0); writer.Write((ushort)0); Text(title);
        void Control(ushort kind, int id, string caption, short y, uint style)
        {
            while (memory.Length % 4 != 0) writer.Write((byte)0);
            writer.Write(hiddenLabels && id != cancelId ? style & ~0x10000000u : style); writer.Write(0u);
            writer.Write((short)10); writer.Write(y); writer.Write((short)100); writer.Write((short)12); writer.Write((ushort)id);
            writer.Write((ushort)0xffff); writer.Write(kind); Text(caption); writer.Write((ushort)0);
        }
        Control(0x0080, cancelId, "Cancel", 50, 0x50010000u);
        if (loginLabels)
        {
            Control(0x0082, 1001, "Login:", 0, 0x50000000u); Control(0x0081, 1002, "fixture-login", 12, 0x50810000u);
            Control(0x0082, 1003, "Password:", 24, 0x50000000u); Control(0x0081, 1004, "fixture-secret", 36, 0x50810020u);
        }
        var bytes = memory.ToArray(); nint template = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, template, bytes.Length); Handle = CreateDialogIndirectParam(0, template, 0, callback, 0); }
        finally { Marshal.FreeHGlobal(template); }
        if (Handle == 0) throw new InvalidOperationException("Native fixture dialog creation failed: " + Marshal.GetLastWin32Error());
    }
    public void Dispose() { if (IsWindow(Handle)) DestroyWindow(Handle); GC.KeepAlive(callback); }
    public void Reopen() => ShowWindow(Handle, 5);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDialogIndirectParam(nint module, nint template, nint parent, DialogProc proc, nint value);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
}
