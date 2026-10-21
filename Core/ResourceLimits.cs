using System.Runtime.InteropServices;
namespace ArmadaAnalyzer;

public static class ResourceLimits
{
    [StructLayout(LayoutKind.Sequential)]
    struct Memory
    {
        public uint Length, Load; public ulong Total, Available, Page, PageAvailable, Virtual, VirtualAvailable, Extended;
    }
    [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref Memory status);
    public static int Workers => Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
    public static int DefaultMemoryMiB => MemoryMiB(256);
    public static int MemoryMiB(int requested)
    {
        var m = new Memory { Length = (uint)Marshal.SizeOf<Memory>() };
        int cap = GlobalMemoryStatusEx(ref m) ? (int)Math.Min(1024UL, m.Total / 4 / 1048576) : 256;
        return Math.Clamp(requested, Math.Min(64, cap), Math.Max(1, cap));
    }
}
