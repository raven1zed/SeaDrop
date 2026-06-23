using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;
using Windows.System;

namespace SeaDropWindows.SeaDrop.shell
{
    [ComVisible(true)]
    [Guid("8F8E3F8A-4B2C-4D5E-9F1A-7C6B5A4D3E2F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerCommand
    {
        void GetTitle(IShellItemArray items, out string title);
        void GetIcon(IShellItemArray items, out string icon);
        void GetToolTip(IShellItemArray items, out string tooltip);
        void GetCanonicalName(out string name);
        void GetState(IShellItemArray items, bool okToBeSlow, out int state);
        void Invoke(IShellItemArray items, IntPtr bindCtx);
        void GetFlags(out int flags);
        void EnumSubCommands(IShellItemArray items, out IEnumExplorerCommand enumCommands);
    }

    [ComVisible(true)]
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    [ClassInterface(ClassInterfaceType.None)]
    public class SeaDropContextMenuHandler : IExplorerCommand
    {
        private const string VerbName = "SeaDrop.Send";
        private const string VerbTitle = "Send via SeaDrop";
        private const string VerbIcon = "seadrop.ico";
        private const string VerbTooltip = "Send file to SeaDrop device";

        public void GetTitle(IShellItemArray items, out string title)
        {
            title = VerbTitle;
        }

        public void GetIcon(IShellItemArray items, out string icon)
        {
            icon = Path.Combine(AppContext.BaseDirectory, "Assets", VerbIcon);
        }

        public void GetToolTip(IShellItemArray items, out string tooltip)
        {
            tooltip = VerbTooltip;
        }

        public void GetCanonicalName(out string name)
        {
            name = VerbName;
        }

        public void GetState(IShellItemArray items, bool okToBeSlow, out int state)
        {
            const int ECS_ENABLED = 0x00000001;
            const int ECS_HIDDEN = 0x00000002;

            if (items == null)
            {
                state = ECS_HIDDEN;
                return;
            }

            items.GetCount(out uint count);
            if (count == 0)
            {
                state = ECS_HIDDEN;
                return;
            }

            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out IShellItem item);
                item.GetAttributes(0xFFFFFFFF, out uint attrs);
                if ((attrs & 0x00000010) != 0) // SFGAO_FOLDER
                {
                    state = ECS_ENABLED;
                    return;
                }
            }

            state = ECS_ENABLED;
        }

        public void Invoke(IShellItemArray items, IntPtr bindCtx)
        {
            items.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out IShellItem item);
                item.GetDisplayName(SIGDN.FILESYSPATH, out string path);
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    SendViaSeaDrop(path);
                }
            }
        }

        public void GetFlags(out int flags)
        {
            flags = 0; // ECF_DEFAULT
        }

        public void EnumSubCommands(IShellItemArray items, out IEnumExplorerCommand enumCommands)
        {
            enumCommands = null!;
        }

        private static async void SendViaSeaDrop(string filePath)
        {
            try
            {
                var uri = new Uri($"seadrop://send/{Uri.EscapeDataString(filePath)}");
                await Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SeaDropContextMenu: {ex.Message}");
            }
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    public interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid rbhid, ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, out IntPtr ppStore);
        void GetPropertyDescriptionList(ref Guid keyType, out IntPtr ppList);
        void GetAttributes(uint dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenum);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    public interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid rbhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    public enum SIGDN : uint
    {
        FILESYSPATH = 0x80058000,
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    public interface IEnumExplorerCommand
    {
        void Next(uint celt, out IExplorerCommand pUICommand, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumExplorerCommand ppenum);
    }
}