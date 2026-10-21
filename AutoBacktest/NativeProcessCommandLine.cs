using System.Runtime.InteropServices;
namespace MT4BatchReporter;
public static class NativeProcessCommandLine
{
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString{public ushort Length,MaximumLength;public IntPtr Buffer;}
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process,int info,IntPtr buffer,int length,out int returned);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CommandLineToArgvW(string commandLine,out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Read(int processId)
    {
        IntPtr handle=OpenProcess(0x1000,false,processId);if(handle==IntPtr.Zero)return "";
        IntPtr buffer=IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(handle,60,IntPtr.Zero,0,out int size);
            if(size<Marshal.SizeOf<UnicodeString>()||size>128*1024)return "";
            buffer=Marshal.AllocHGlobal(size);
            if(NtQueryInformationProcess(handle,60,buffer,size,out _)!=0)return "";
            var str=Marshal.PtrToStructure<UnicodeString>(buffer);
            long offset=str.Buffer.ToInt64()-buffer.ToInt64();
            if(offset<0||offset+str.Length>size||str.Length%2!=0)return "";
            return Marshal.PtrToStringUni(str.Buffer,str.Length/2)??"";
        }
        finally{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);CloseHandle(handle);}
    }
    public static bool HasArgument(string commandLine,string expectedConfig)
    {
        if(string.IsNullOrWhiteSpace(commandLine))return false;
        IntPtr args=CommandLineToArgvW(commandLine,out int count);if(args==IntPtr.Zero)return false;
        try{for(int i=1;i<count;i++){string? arg=Marshal.PtrToStringUni(Marshal.ReadIntPtr(args,i*IntPtr.Size));if(arg?.Equals(expectedConfig,StringComparison.OrdinalIgnoreCase)==true)return true;}return false;}
        finally{LocalFree(args);}
    }
}
