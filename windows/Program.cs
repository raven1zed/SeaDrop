using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using WinRT;

namespace SeaDropWindows;

public static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BootstrapInit(ulong majorMinorVersion, IntPtr versionTag, ushort reserved, int options);

    [MTAThread]
    static void Main()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "seadrop_startup.log");
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] Main started, BaseDir={AppContext.BaseDirectory}\n");

            var bootstrapPath = Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.Bootstrap.dll");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] Bootstrap exists: {File.Exists(bootstrapPath)}\n");

            var hMod = LoadLibraryW(bootstrapPath);
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] LoadLibrary: 0x{hMod:X}\n");

            if (hMod != IntPtr.Zero)
            {
                var pInit = GetProcAddress(hMod, "MddBootstrapInitialize2");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] MddBootstrapInitialize2: 0x{pInit:X}\n");

                if (pInit != IntPtr.Zero)
                {
                    var init = Marshal.GetDelegateForFunctionPointer<BootstrapInit>(pInit);
                    var hr = init(0x00010006, IntPtr.Zero, 0, 0);
                    File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] Bootstrap hr=0x{hr:X8}\n");
                    if (hr != 0) return;
                }
            }

            ComWrappersSupport.InitializeComWrappers();
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] ComWrappers OK\n");

            Application.Start(p =>
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] Application.Start callback\n");
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
                new App();
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] App created\n");
            });
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] Done\n");
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] FATAL: {ex}\n"); } catch { }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
}
