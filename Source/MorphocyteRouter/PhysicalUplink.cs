using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MorphocyteRouter;

internal static class PhysicalUplink
{
    // MIB_IF_ROW2 prefix, as documented by Microsoft. The native call writes the
    // complete row into an oversized buffer; offsets are computed by the CLR.
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct InterfacePrefix
    {
        public ulong Luid; public uint Index; public Guid Guid;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=257)] public string Description;
        public uint AddressLength;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] Address;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] PermanentAddress;
        public uint Mtu,Type,TunnelType,MediaType,PhysicalMediumType,AccessType,DirectionType;
        public byte Flags;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetIfEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination,out uint index);

    internal static bool IsHardware(int index)
    {
        var row=Marshal.AllocHGlobal(2048);
        try
        {
            Marshal.Copy(new byte[2048],0,row,2048);
            Marshal.WriteInt32(row,(int)Marshal.OffsetOf<InterfacePrefix>(nameof(InterfacePrefix.Index)),index);
            return GetIfEntry2(row)==0&&(Marshal.ReadByte(row,(int)Marshal.OffsetOf<InterfacePrefix>(nameof(InterfacePrefix.Flags)))&1)!=0;
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    internal static string? Find()
    {
        if(!OperatingSystem.IsWindows()) return null;
        try
        {
            var candidates=NetworkInterface.GetAllNetworkInterfaces().Where(adapter=>adapter.OperationalStatus==OperationalStatus.Up
                && adapter.Supports(NetworkInterfaceComponent.IPv4)
                && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(adapter=>(Adapter:adapter,Properties:adapter.GetIPProperties()))
                .Where(item=>item.Properties.GetIPv4Properties() is { } ipv4&&IsHardware(ipv4.Index)
                    && item.Properties.GatewayAddresses.Any(gateway=>gateway.Address.AddressFamily==AddressFamily.InterNetwork
                        && !gateway.Address.Equals(IPAddress.Any)&&!IPAddress.IsLoopback(gateway.Address)))
                .ToArray();
            if(GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes()),out var preferred)==0)
                foreach(var candidate in candidates)
                    if(candidate.Properties.GetIPv4Properties()!.Index==preferred) return candidate.Adapter.Name;
            // If another VPN owns the OS default route, use the unambiguous
            // physical gateway. With several candidates retain auto detection.
            return candidates.Length==1?candidates[0].Adapter.Name:null;
        }
        catch(NetworkInformationException) { return null; }
        catch(DllNotFoundException) { return null; }
        catch(EntryPointNotFoundException) { return null; }
    }
}
