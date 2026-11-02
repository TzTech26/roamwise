using System.Runtime.InteropServices;
using System.Text;
using Roamwise.Native;

namespace Roamwise.Wifi;

public sealed class WlanException(uint code, string operation) : Exception(Describe(code, operation))
{
    public uint Code { get; } = code;

    /// <summary>Windows blocks Wi-Fi scan results when location access is off for desktop apps.</summary>
    public bool IsLocationDenied => Code == WlanNative.ErrorAccessDenied;

    private static string Describe(uint code, string op) => code switch
    {
        WlanNative.ErrorAccessDenied => "Windows blocked access to Wi-Fi details. Turn on location access for desktop apps.",
        WlanNative.ErrorServiceNotActive => "The WLAN AutoConfig service isn't running.",
        WlanNative.ErrorNdisDot11PowerStateInvalid => "Wi-Fi is turned off.",
        _ => $"{op} failed (error {code}).",
    };
}

/// <summary>Managed wrapper over the Native Wifi API. Thread-safe for the calls Roamwise makes.</summary>
public sealed class WlanClient : IDisposable
{
    private readonly IntPtr _handle;
    private readonly WlanNative.WlanNotificationCallback _callback; // kept alive for the native side
    private readonly object _waitLock = new();
    private readonly List<Waiter> _waiters = [];

    /// <summary>Raised on a thread-pool thread when the adapter connects, disconnects or roams.</summary>
    public event Action<Guid>? ConnectionChanged;

    public WlanClient()
    {
        Check(WlanNative.WlanOpenHandle(WlanNative.ClientVersion2, IntPtr.Zero, out _, out _handle), "Opening Wi-Fi");
        _callback = OnNotification;
        Check(WlanNative.WlanRegisterNotification(_handle, WlanNotificationSource.Acm | WlanNotificationSource.Msm, true,
            _callback, IntPtr.Zero, IntPtr.Zero, out _), "Subscribing to Wi-Fi events");
    }

    public unsafe IReadOnlyList<WifiAdapter> GetAdapters()
    {
        Check(WlanNative.WlanEnumInterfaces(_handle, IntPtr.Zero, out var list), "Listing Wi-Fi adapters");
        try
        {
            uint count = *(uint*)list;
            var items = (WlanInterfaceInfo*)(list + 8);
            var result = new List<WifiAdapter>((int)count);
            for (int i = 0; i < count; i++)
            {
                var info = items[i];
                result.Add(new WifiAdapter(info.InterfaceGuid, new string(info.Description), info.State == WlanInterfaceState.Connected));
            }
            return result;
        }
        finally { WlanNative.WlanFreeMemory(list); }
    }

    /// <summary>Asks the driver for a fresh scan and waits for it to finish (or for the timeout).</summary>
    public async Task ScanAsync(Guid iface, TimeSpan timeout, CancellationToken ct)
    {
        var wait = WaitFor(iface, [(int)WlanNotificationAcm.ScanComplete, (int)WlanNotificationAcm.ScanFail]);
        uint rc = WlanNative.WlanScan(_handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (rc != WlanNative.ErrorSuccess)
        {
            Cancel(wait);
            // A scan already in progress reports ERROR_BUSY-like states; the cached list is still useful then.
            if (rc == WlanNative.ErrorAccessDenied) throw new WlanException(rc, "Scanning");
            return;
        }
        await Task.WhenAny(wait.Tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        Cancel(wait);
    }

    public unsafe IReadOnlyList<AccessPoint> GetAccessPoints(Guid iface)
    {
        Check(WlanNative.WlanGetNetworkBssList(_handle, ref iface, IntPtr.Zero, Dot11BssType.Infrastructure, false, IntPtr.Zero, out var list),
            "Reading nearby access points");
        try
        {
            uint count = *(uint*)(list + 4);
            var result = new List<AccessPoint>((int)count);
            byte* first = (byte*)(list + 8);
            for (int i = 0; i < count; i++)
            {
                byte* p = first + i * sizeof(WlanBssEntry);
                var e = (WlanBssEntry*)p;
                var ies = new ReadOnlySpan<byte>(p + e->IeOffset, (int)e->IeSize);
                var parsed = InfoElements.Parse(ies);
                int mhz = (int)(e->ChCenterFrequency / 1000);
                var (band, channel) = InfoElements.FromFrequency(mhz);

                result.Add(new AccessPoint
                {
                    Ssid = DecodeSsid(&e->Ssid),
                    BssidBytes = new ReadOnlySpan<byte>(e->Bssid, 6).ToArray(),
                    Rssi = e->Rssi,
                    LinkQuality = (int)e->LinkQuality,
                    FrequencyMHz = mhz,
                    Band = band,
                    Channel = channel,
                    WidthMHz = band == WifiBand.Ghz24 ? Math.Min(parsed.WidthMHz, 40) : parsed.WidthMHz,
                    Generation = e->PhyType switch
                    {
                        Dot11PhyType.Eht => WifiGeneration.Wifi7,
                        Dot11PhyType.He => WifiGeneration.Wifi6,
                        Dot11PhyType.Vht => WifiGeneration.Wifi5,
                        Dot11PhyType.Ht => WifiGeneration.Wifi4,
                        _ => WifiGeneration.Legacy,
                    },
                    ChannelUtilization = parsed.ChannelUtilization,
                    StationCount = parsed.StationCount,
                    BroadcastName = parsed.Name,
                    Manufacturer = parsed.Manufacturer,
                    Model = parsed.Model,
                    DeviceMac = parsed.DeviceMac,
                    VendorElements = parsed.VendorElements,
                });
            }
            return result;
        }
        finally { WlanNative.WlanFreeMemory(list); }
    }

    public unsafe WifiConnection? GetConnection(Guid iface)
    {
        uint rc = WlanNative.WlanQueryInterface(_handle, ref iface, WlanIntfOpcode.CurrentConnection, IntPtr.Zero, out _, out var data, out _);
        if (rc == WlanNative.ErrorInvalidState) return null; // not connected
        Check(rc, "Reading the current connection");
        try
        {
            var a = (WlanConnectionAttributes*)data;
            if (a->State != WlanInterfaceState.Connected) return null;
            return new WifiConnection
            {
                ProfileName = new string(a->ProfileName),
                Ssid = DecodeSsid(&a->Association.Ssid),
                BssidBytes = new ReadOnlySpan<byte>(a->Association.Bssid, 6).ToArray(),
                SignalQuality = (int)a->Association.SignalQuality,
                RxRateMbps = (int)(a->Association.RxRate / 1000),
                TxRateMbps = (int)(a->Association.TxRate / 1000),
            };
        }
        finally { WlanNative.WlanFreeMemory(data); }
    }

    /// <summary>Live RSSI of the current link, or null when not connected.</summary>
    public unsafe int? GetRssi(Guid iface)
    {
        uint rc = WlanNative.WlanQueryInterface(_handle, ref iface, WlanIntfOpcode.Rssi, IntPtr.Zero, out _, out var data, out _);
        if (rc != WlanNative.ErrorSuccess) return null;
        try { return *(int*)data; }
        finally { WlanNative.WlanFreeMemory(data); }
    }

    public unsafe IReadOnlyList<string> GetProfileNames(Guid iface)
    {
        Check(WlanNative.WlanGetProfileList(_handle, ref iface, IntPtr.Zero, out var list), "Reading saved networks");
        try
        {
            uint count = *(uint*)list;
            var items = (WlanProfileInfo*)(list + 8);
            var names = new List<string>((int)count);
            for (int i = 0; i < count; i++) names.Add(new string(items[i].ProfileName));
            return names;
        }
        finally { WlanNative.WlanFreeMemory(list); }
    }

    /// <summary>
    /// Reconnects using the saved profile but pins the association to one BSSID.
    /// Returns once Windows reports the attempt finished (or the timeout passes).
    /// </summary>
    public async Task<bool> ConnectToBssidAsync(Guid iface, string profileName, byte[] bssid, TimeSpan timeout, CancellationToken ct)
    {
        // DOT11_BSSID_LIST: NDIS_OBJECT_HEADER (4) + uNumOfEntries (4) + uTotalNumOfEntries (4) + one MAC (6), padded to 20.
        const int listSize = 20;
        IntPtr bssidList = Marshal.AllocHGlobal(listSize);
        IntPtr profile = Marshal.StringToHGlobalUni(profileName);
        var wait = WaitFor(iface, [(int)WlanNotificationAcm.ConnectionComplete, (int)WlanNotificationAcm.ConnectionAttemptFail]);
        try
        {
            var list = new byte[listSize];
            list[0] = 0x80; // NDIS_OBJECT_TYPE_DEFAULT
            list[1] = 1;    // DOT11_BSSID_LIST_REVISION_1
            BitConverter.TryWriteBytes(list.AsSpan(2), (ushort)listSize);
            BitConverter.TryWriteBytes(list.AsSpan(4), 1u); // uNumOfEntries
            BitConverter.TryWriteBytes(list.AsSpan(8), 1u); // uTotalNumOfEntries
            bssid.CopyTo(list, 12);
            Marshal.Copy(list, 0, bssidList, listSize);

            var parameters = new WlanConnectionParameters
            {
                ConnectionMode = 0,
                Profile = profile,
                Dot11Ssid = IntPtr.Zero,
                DesiredBssidList = bssidList,
                BssType = Dot11BssType.Infrastructure,
                Flags = 0,
            };
            Check(WlanNative.WlanConnect(_handle, ref iface, ref parameters, IntPtr.Zero), "Switching access point");

            var done = await Task.WhenAny(wait.Tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            return done == wait.Tcs.Task && wait.Tcs.Task.Result == (int)WlanNotificationAcm.ConnectionComplete;
        }
        finally
        {
            Cancel(wait);
            Marshal.FreeHGlobal(profile);
            Marshal.FreeHGlobal(bssidList);
        }
    }

    /// <summary>Sets a BOOL interface option. These reset to defaults when Roamwise closes its handle.</summary>
    public unsafe uint SetOption(Guid iface, WlanIntfOpcodeOption option, bool value)
    {
        int v = value ? 1 : 0;
        var opcode = option == WlanIntfOpcodeOption.BackgroundScan ? WlanIntfOpcode.BackgroundScanEnabled : WlanIntfOpcode.MediaStreamingMode;
        return WlanNative.WlanSetInterface(_handle, ref iface, opcode, sizeof(int), (IntPtr)(&v), IntPtr.Zero);
    }

    private sealed record Waiter(Guid Iface, int[] Codes, TaskCompletionSource<int> Tcs);

    private Waiter WaitFor(Guid iface, int[] codes)
    {
        var w = new Waiter(iface, codes, new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_waitLock) _waiters.Add(w);
        return w;
    }

    private void Cancel(Waiter w)
    {
        lock (_waitLock) _waiters.Remove(w);
    }

    private void OnNotification(IntPtr ptr, IntPtr context)
    {
        var n = Marshal.PtrToStructure<WlanNotificationData>(ptr);

        if (n.Source == WlanNotificationSource.Acm)
        {
            lock (_waitLock)
            {
                foreach (var w in _waiters.Where(w => w.Iface == n.InterfaceGuid && w.Codes.Contains(n.Code)))
                    w.Tcs.TrySetResult(n.Code);
            }
        }

        bool changed = (n.Source == WlanNotificationSource.Acm &&
                        n.Code is (int)WlanNotificationAcm.ConnectionComplete or (int)WlanNotificationAcm.Disconnected
                            or (int)WlanNotificationAcm.InterfaceArrival or (int)WlanNotificationAcm.InterfaceRemoval)
                       || (n.Source == WlanNotificationSource.Msm && n.Code == (int)WlanNotificationMsm.RoamingEnd);
        if (changed) ConnectionChanged?.Invoke(n.InterfaceGuid);
    }

    private static unsafe string DecodeSsid(Dot11Ssid* s)
    {
        int len = (int)Math.Min(s->Length, 32);
        return Encoding.UTF8.GetString(s->Ssid, len);
    }

    private static void Check(uint rc, string op)
    {
        if (rc != WlanNative.ErrorSuccess) throw new WlanException(rc, op);
    }

    public void Dispose()
    {
        WlanNative.WlanRegisterNotification(_handle, WlanNotificationSource.None, true, null, IntPtr.Zero, IntPtr.Zero, out _);
        WlanNative.WlanCloseHandle(_handle, IntPtr.Zero);
    }
}

public enum WlanIntfOpcodeOption { BackgroundScan, MediaStreaming }
