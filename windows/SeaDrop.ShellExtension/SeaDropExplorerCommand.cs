using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SeaDrop.ShellExtension
{
    // IExplorerCommand — per Windows 11 spec, this places the command in the
    // top-level right-click menu (not buried under "Show more options").
    // Communicates with the main SeaDrop app via the named pipe
    // \\.\pipe\SeaDropShell. The shell extension itself has ZERO TCP knowledge.
    [ComVisible(true)]
    [Guid("E85D2F3A-7C9E-4D6B-9F1A-2B3C4D5E6F70")]
    [ClassInterface(ClassInterfaceType.None)]
    public class SeaDropExplorerCommand : IExplorerCommand
    {
        private const string PipeName = "SeaDropShell";

        public int GetTitle(IShellItemArray items, out string title)
        {
            title = "Send via SeaDrop";
            return 0; // S_OK
        }

        public int GetIcon(IShellItemArray items, out string icon)
        {
            icon = "%ProgramFiles%\\SeaDrop\\SeaDrop.ico,0";
            return 0;
        }

        public int GetToolTip(IShellItemArray items, out string tooltip)
        {
            tooltip = "Send the selected file(s) to your Android phone via SeaDrop";
            return 0;
        }

        public int GetCanonicalName(out string name)
        {
            // Unique GUID — required so Windows can persist the command
            name = "SeaDropSendFile";
            return 0;
        }

        public int GetState(IShellItemArray items, bool okToBeSlow, out uint state)
        {
            // ECS_ENABLED (0x0) — always enabled when files are selected
            state = 0;
            return 0;
        }

        public int Invoke(IShellItemArray items, object bindContext)
        {
            try
            {
                uint count;
                items.GetCount(out count);
                if (count == 0) return 0;

                var paths = new StringBuilder();
                for (uint i = 0; i < count; i++)
                {
                    IShellItem item;
                    items.GetItemAt(i, out item);
                    if (item == null) continue;

                    IntPtr pszPath;
                    item.GetDisplayName(SIGDN.FILESYSPATH, out pszPath);
                    if (pszPath == IntPtr.Zero) continue;

                    string path;
                    try { path = Marshal.PtrToStringAuto(pszPath); }
                    finally { Marshal.FreeCoTaskMem(pszPath); }

                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                    if (paths.Length > 0) paths.Append('\n');
                    paths.Append(path);
                }

                if (paths.Length == 0) return 0;

                // Send to running SeaDrop via named pipe. If no listener, the
                // pipe connect fails — we silently no-op. SeaDrop will pick up
                // the file via Settings → Send a file, or via the share sheet.
                var message = paths.ToString();
                _ = SendToPipeAsync(message);
                return 0;
            }
            catch
            {
                // Never crash Explorer
                return 0;
            }
        }

        private static async Task SendToPipeAsync(string message)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                await client.ConnectAsync(2000);
                using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteAsync(message);
            }
            catch
            {
                // SeaDrop not running — silently ignore. Explorer right-click
                // should never show an error dialog.
            }
        }

        // ── IShellItemArray interop ──────────────────────────────
        [DllImport("shell32.dll")]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IShellItem ppv);
    }

    // ── COM interfaces (raw definitions to avoid extra package refs) ──

    public enum SIGDN : uint
    {
        FILESYSPATH = 0x80058000
    }

    [ComImport] 
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37f65")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoFlags);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr pkey, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetAttributes(uint AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [ComImport]
    [Guid("a08a4d8e-9c20-4d52-bb6b-2b457f4297d3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerCommand
    {
        int GetTitle(IShellItemArray items, out string title);
        int GetIcon(IShellItemArray items, out string icon);
        int GetToolTip(IShellItemArray items, out string tooltip);
        int GetCanonicalName(out string name);
        int GetState(IShellItemArray items, bool okToBeSlow, out uint state);
        int Invoke(IShellItemArray items, object bindContext);
    }
}
