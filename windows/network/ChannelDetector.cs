using System;
using System.Runtime.InteropServices;

namespace SeaDropWindows.SeaDrop.network
{
    public class ChannelDetector
    {
        private const string WlanapiDll = "wlanapi.dll";

        [DllImport(WlanapiDll, ExactSpelling = true)]
        private static extern int WlanOpenHandle(
            int dwClientVersion,
            IntPtr pReserved,
            out int pdwNegotiatedVersion,
            out IntPtr phClientHandle);

        [DllImport(WlanapiDll, ExactSpelling = true)]
        private static extern int WlanCloseHandle(
            IntPtr hClientHandle,
            IntPtr pReserved);

        [DllImport(WlanapiDll, ExactSpelling = true)]
        private static extern int WlanEnumInterfaces(
            IntPtr hClientHandle,
            IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport(WlanapiDll, ExactSpelling = true)]
        private static extern int WlanQueryInterface(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            WlanIntfOpcode OpCode,
            IntPtr pReserved,
            out int pdwDataSize,
            out IntPtr ppData,
            IntPtr pWlanOpcodeValueType);

        [DllImport(WlanapiDll, ExactSpelling = true)]
        private static extern int WlanFreeMemory(IntPtr pMemory);

        private enum WlanIntfOpcode
        {
            ChannelNumber = 51,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WlanInterfaceInfoList
        {
            public int dwNumberOfItems;
            public int dwIndex;
            public WlanInterfaceInfo[] InterfaceInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public WlanInterfaceState isState;
        }

        private enum WlanInterfaceState
        {
            NotReady = 0,
            Connected = 1,
            AdHocNetworkFormed = 2,
            Disconnecting = 3,
            Disconnected = 4,
            Associating = 5,
            Discovering = 6,
            Authenticating = 7
        }

        public int GetHomeWiFiChannel()
        {
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr interfaceListPtr = IntPtr.Zero;
            IntPtr dataPtr = IntPtr.Zero;

            try
            {
                int negotiatedVersion;
                int result = WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out clientHandle);
                if (result != 0) return 6;

                result = WlanEnumInterfaces(clientHandle, IntPtr.Zero, out interfaceListPtr);
                if (result != 0) return 6;

                var interfaceList = Marshal.PtrToStructure<WlanInterfaceInfoList>(interfaceListPtr);
                var interfaces = new WlanInterfaceInfo[interfaceList.dwNumberOfItems];
                IntPtr currentPtr = interfaceListPtr + Marshal.SizeOf<WlanInterfaceInfoList>();
                for (int i = 0; i < interfaceList.dwNumberOfItems; i++)
                {
                    interfaces[i] = Marshal.PtrToStructure<WlanInterfaceInfo>(currentPtr);
                    currentPtr += Marshal.SizeOf<WlanInterfaceInfo>();
                }

                foreach (var iface in interfaces)
                {
                    if (iface.isState == WlanInterfaceState.Connected)
                    {
                        var guid = iface.InterfaceGuid;
                        result = WlanQueryInterface(
                            clientHandle,
                            ref guid,
                            WlanIntfOpcode.ChannelNumber,
                            IntPtr.Zero,
                            out int dataSize,
                            out dataPtr,
                            IntPtr.Zero);

                        if (result == 0 && dataPtr != IntPtr.Zero)
                        {
                            int channel = Marshal.ReadInt32(dataPtr);
                            return channel;
                        }
                    }
                }
                return 6;
            }
            catch
            {
                return 6;
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) WlanFreeMemory(dataPtr);
                if (interfaceListPtr != IntPtr.Zero) WlanFreeMemory(interfaceListPtr);
                if (clientHandle != IntPtr.Zero) WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }
    }
}