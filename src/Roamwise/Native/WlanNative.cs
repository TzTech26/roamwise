using System.Runtime.InteropServices;

namespace Roamwise.Native;

// P/Invoke surface for the Native Wifi API (wlanapi.dll).
// Struct layouts mirror wlanapi.h / windot11.h; they are blittable so we read them straight from native memory.
internal static unsafe class WlanNative
{
    private const string Dll = "wlanapi.dll";

    public const uint ClientVersion2 = 2;
    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorInvalidState = 5023;
    public const uint ErrorServiceNotActive = 1062;
    public const uint ErrorNdisDot11PowerStateInvalid = 0x80342002;

    [DllImport(Dll)]
    public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport(Dll)]
    public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport(Dll)]
    public static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport(Dll)]
    public static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport(Dll)]
    public static extern uint WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

    [DllImport(Dll)]
    public static extern uint WlanGetNetworkBssList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid,
        Dot11BssType dot11BssType, [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled, IntPtr pReserved, out IntPtr ppWlanBssList);

    [DllImport(Dll)]
    public static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WlanIntfOpcode opCode, IntPtr pReserved,
        out uint pdwDataSize, out IntPtr ppData, out int pWlanOpcodeValueType);

    [DllImport(Dll)]
    public static extern uint WlanSetInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WlanIntfOpcode opCode, uint dwDataSize, IntPtr pData, IntPtr pReserved);

    [DllImport(Dll)]
    public static extern uint WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, ref WlanConnectionParameters pConnectionParameters, IntPtr pReserved);

    [DllImport(Dll)]
    public static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved, out IntPtr ppProfileList);

    [DllImport(Dll)]
    public static extern uint WlanRegisterNotification(IntPtr hClientHandle, WlanNotificationSource dwNotifSource,
        [MarshalAs(UnmanagedType.Bool)] bool bIgnoreDuplicate, WlanNotificationCallback? funcCallback, IntPtr pCallbackContext,
        IntPtr pReserved, out WlanNotificationSource pdwPrevNotifSource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WlanNotificationCallback(IntPtr data, IntPtr context);
}

internal enum Dot11BssType { Infrastructure = 1, Independent = 2, Any = 3 }

internal enum Dot11PhyType : uint
{
    Unknown = 0, Fhss = 1, Dsss = 2, IrBaseband = 3, Ofdm = 4, HrDsss = 5, Erp = 6,
    Ht = 7, Vht = 8, Dmg = 9, He = 10, Eht = 11,
}

internal enum WlanInterfaceState
{
    NotReady = 0, Connected = 1, AdHocNetworkFormed = 2, Disconnecting = 3,
    Disconnected = 4, Associating = 5, Discovering = 6, Authenticating = 7,
}

internal enum WlanIntfOpcode : uint
{
    BackgroundScanEnabled = 2,
    MediaStreamingMode = 3,
    InterfaceState = 6,
    CurrentConnection = 7,
    ChannelNumber = 8,
    Rssi = 0x10000102,
}

[Flags]
internal enum WlanNotificationSource : uint
{
    None = 0, OneX = 0x4, Acm = 0x8, Msm = 0x10, Security = 0x20, Ihv = 0x40, All = 0xFFFF,
}

internal enum WlanNotificationAcm
{
    ScanComplete = 7,
    ScanFail = 8,
    ConnectionStart = 9,
    ConnectionComplete = 10,
    ConnectionAttemptFail = 11,
    InterfaceArrival = 13,
    InterfaceRemoval = 14,
    Disconnected = 21,
}

internal enum WlanNotificationMsm
{
    Associated = 2,
    Connected = 3,
    RoamingEnd = 9,
    Disconnected = 10,
    SignalQualityChange = 11,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Dot11Ssid
{
    public uint Length;
    public fixed byte Ssid[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlanInterfaceInfo
{
    public Guid InterfaceGuid;
    public fixed char Description[256];
    public WlanInterfaceState State;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlanRateSet
{
    public uint Length;
    public fixed ushort Rates[126];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlanBssEntry
{
    public Dot11Ssid Ssid;
    public uint PhyId;
    public fixed byte Bssid[6];
    public Dot11BssType BssType;
    public Dot11PhyType PhyType;
    public int Rssi;
    public uint LinkQuality;
    public byte InRegDomain;
    public ushort BeaconPeriod;
    public ulong Timestamp;
    public ulong HostTimestamp;
    public ushort CapabilityInformation;
    public uint ChCenterFrequency; // kHz
    public WlanRateSet RateSet;
    public uint IeOffset; // relative to the start of this entry
    public uint IeSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlanAssociationAttributes
{
    public Dot11Ssid Ssid;
    public Dot11BssType BssType;
    public fixed byte Bssid[6];
    public Dot11PhyType PhyType;
    public uint PhyIndex;
    public uint SignalQuality; // 0-100
    public uint RxRate; // kbps
    public uint TxRate; // kbps
}

[StructLayout(LayoutKind.Sequential)]
internal struct WlanSecurityAttributes
{
    public int SecurityEnabled;
    public int OneXEnabled;
    public uint AuthAlgorithm;
    public uint CipherAlgorithm;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlanConnectionAttributes
{
    public WlanInterfaceState State;
    public int ConnectionMode;
    public fixed char ProfileName[256];
    public WlanAssociationAttributes Association;
    public WlanSecurityAttributes Security;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WlanConnectionParameters
{
    public int ConnectionMode; // 0 = wlan_connection_mode_profile
    public IntPtr Profile;     // LPCWSTR
    public IntPtr Dot11Ssid;   // PDOT11_SSID
    public IntPtr DesiredBssidList; // PDOT11_BSSID_LIST
    public Dot11BssType BssType;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WlanNotificationData
{
    public WlanNotificationSource Source;
    public int Code;
    public Guid InterfaceGuid;
    public uint DataSize;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlanProfileInfo
{
    public fixed char ProfileName[256];
    public uint Flags;
}
