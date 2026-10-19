using System;
using System.Runtime.InteropServices;
public static class StartupSymbolResolver
{
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("dbghelp.dll", SetLastError=true)] static extern bool SymInitialize(IntPtr process, string path, bool invade);
    [DllImport("dbghelp.dll")] static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", SetLastError=true)] static extern ulong SymLoadModuleEx(IntPtr process, IntPtr file, string image, string name, ulong address, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll", SetLastError=true)] static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
    [DllImport("dbghelp.dll")] static extern bool SymCleanup(IntPtr process);
    public static void Resolve(string image, string path, ulong[] offsets)
    {
        IntPtr process=GetCurrentProcess(); SymSetOptions(0x16);
        if(!SymInitialize(process,path,false)) throw new Exception("Symbol initialization: "+Marshal.GetLastWin32Error());
        try {
            ulong baseAddress=SymLoadModuleEx(process,IntPtr.Zero,image,null,0x10000000,0,IntPtr.Zero,0);
            if(baseAddress==0) throw new Exception("Symbol load: "+Marshal.GetLastWin32Error());
            foreach(ulong offset in offsets) {
                IntPtr symbol=Marshal.AllocHGlobal(88+2048);
                try {
                    for(int i=0;i<88+2048;i++) Marshal.WriteByte(symbol,i,0);
                    Marshal.WriteInt32(symbol,0,88); Marshal.WriteInt32(symbol,80,2047);
                    ulong displacement;
                    if(SymFromAddr(process,baseAddress+offset-1,out displacement,symbol))
                        Console.WriteLine("0x"+offset.ToString("x")+" "+Marshal.PtrToStringAnsi(IntPtr.Add(symbol,84))+" +0x"+displacement.ToString("x"));
                    else Console.WriteLine("Unresolved 0x"+offset.ToString("x")+" error "+Marshal.GetLastWin32Error());
                } finally {Marshal.FreeHGlobal(symbol);}
            }
        } finally {SymCleanup(process);}
    }
}
