#!/usr/bin/env python3
"""Mock NetworkManager and (optionally) wpa_supplicant on the bus in DBUS_SESSION_BUS_ADDRESS, for CI.

    mock_wifi.py nm            start NetworkManager with one Wi-Fi device on "Office", three access points
    mock_wifi.py nm+wpa        the same plus wpa_supplicant (dBm, beacon data, Roam)
    mock_wifi.py nm+iwd        NetworkManager on iwd: the connected access point plus one of iwd's per-network
                               placeholders (00:01:02:..., 2417 MHz); wpa_supplicant runs but manages nothing

Prints READY once everything is registered, then serves until killed.
"""
import signal
import sys
import time

import dbus
import dbusmock
from dbusmock import DBusTestCase

MODE = sys.argv[1] if len(sys.argv) > 1 else "nm"
WITH_WPA = "wpa" in MODE
WITH_IWD = "iwd" in MODE

# BSSID, frequency (MHz), NetworkManager strength (%), supplicant signal (dBm)
APS = [
    ("3c:28:6d:a4:f1:20", 5220, 45, -71),   # connected, 5 GHz ch 44
    ("3c:28:6d:b2:07:9c", 5745, 80, -54),   # better, 5 GHz ch 149
    ("3c:28:6d:a4:f1:21", 2437, 55, -66),   # 2.4 GHz ch 6
]

nm_proc, nm_obj = DBusTestCase.spawn_server_template("networkmanager", {}, stdout=sys.stderr, system_bus=False)
nm = dbus.Interface(nm_obj, dbusmock.MOCK_IFACE)
servers = [nm_proc]


def stop(*_):
    # The mock servers are separate processes; take them down with us so the next run can claim the names.
    for server in servers:
        server.terminate()
        server.wait()
    sys.exit(0)


signal.signal(signal.SIGTERM, stop)
signal.signal(signal.SIGINT, stop)
device = nm.AddWiFiDevice("mock_wifi0", "wlan0", 100)
# Through iwd, NetworkManager sees one access point per network: the real one you're on, placeholders for the rest.
aps = [nm.AddAccessPoint(device, f"ap{i}", "Office", bssid, 2, freq, 54000, strength, 0x100)
       for i, (bssid, freq, strength, _) in enumerate(APS[:1] if WITH_IWD else APS)]
if WITH_IWD:
    nm.AddAccessPoint(device, "ap9", "Cafe", "00:01:02:00:00:01", 2, 2417, 54000, 70, 0x100)
connection = nm.AddWiFiConnection(device, "Office", "Office", "wpa-psk")
nm.AddActiveConnection([device], connection, aps[0], "Office", 2)
device_obj = dbus.SessionBus().get_object("org.freedesktop.NetworkManager", device)
props = dbus.Interface(device_obj, dbus.PROPERTIES_IFACE)
device_mock = dbus.Interface(device_obj, dbusmock.MOCK_IFACE)


def set_prop(interface, name, value):
    # The template only defines some properties; add the rest.
    try:
        props.Set(interface, name, value)
    except dbus.exceptions.DBusException:
        device_mock.AddProperty(interface, name, value)


set_prop("org.freedesktop.NetworkManager.Device.Wireless", "ActiveAccessPoint", dbus.ObjectPath(aps[0]))
set_prop("org.freedesktop.NetworkManager.Device.Wireless", "Bitrate", dbus.UInt32(286000))
set_prop("org.freedesktop.NetworkManager.Device", "State", dbus.UInt32(100))

# Real NetworkManager objects carry nested types the template leaves out; include some so parsing them is tested.
set_prop("org.freedesktop.NetworkManager.Device", "LldpNeighbors", dbus.Array(
    [dbus.Dictionary({"chassis-id": "00:11:22:33:44:55", "port-id": "gi0/1", "destination": "nearest-bridge"}, signature="sv")],
    signature="a{sv}"))
ip6 = dbus.ByteArray(bytes.fromhex("fd000000000000000000000000000001"))
dbus.Interface(dbus.SessionBus().get_object("org.freedesktop.NetworkManager", "/org/freedesktop/NetworkManager/Settings"),
               "org.freedesktop.NetworkManager.Settings").AddConnection(dbus.Dictionary({
    "connection": dbus.Dictionary({"id": "Home", "type": "802-11-wireless", "uuid": "0c5c8a3e-6f0e-4c2b-9d4e-1f2a3b4c5d6e",
                                   "permissions": dbus.Array([], signature="s")}, signature="sv"),
    "802-11-wireless": dbus.Dictionary({"ssid": dbus.ByteArray(b"Home"), "mode": "infrastructure",
                                        "seen-bssids": dbus.Array(["aa:bb:cc:dd:ee:ff"], signature="s")}, signature="sv"),
    "ipv4": dbus.Dictionary({"method": "manual",
                             "addresses": dbus.Array([dbus.Array([dbus.UInt32(0x6401a8c0), dbus.UInt32(24), dbus.UInt32(0x0101a8c0)], signature="u")], signature="au"),
                             "address-data": dbus.Array([dbus.Dictionary({"address": "192.168.1.100", "prefix": dbus.UInt32(24)}, signature="sv")], signature="a{sv}"),
                             "dns": dbus.Array([dbus.UInt32(0x0101a8c0)], signature="u")}, signature="sv"),
    "ipv6": dbus.Dictionary({"method": "auto",
                             "addresses": dbus.Array([dbus.Struct((ip6, dbus.UInt32(64), ip6), signature="ayuay")], signature="(ayuay)")}, signature="sv"),
}, signature="sa{sv}"))

if WITH_WPA:
    wpa_proc = DBusTestCase.spawn_server("fi.w1.wpa_supplicant1", "/fi/w1/wpa_supplicant1",
                                                  "fi.w1.wpa_supplicant1", system_bus=False, stdout=sys.stderr)
    servers.append(wpa_proc)
    wpa_obj = dbus.SessionBus().get_object("fi.w1.wpa_supplicant1", "/fi/w1/wpa_supplicant1")
    wpa = dbus.Interface(wpa_obj, dbusmock.MOCK_IFACE)
    iface = "/fi/w1/wpa_supplicant1/Interfaces/0"
    wpa.AddMethod("fi.w1.wpa_supplicant1", "GetInterface", "s", "o", f"ret = dbus.ObjectPath('{iface}')")

    def ies(channel_util):
        # BSS Load (stations 9, utilisation), HT operation (40 MHz), VHT operation (80 MHz), HE operation (Wi-Fi 6)
        return ([11, 5, 9, 0, channel_util, 0, 0]
                + [61, 22, 44, 0x05] + [0] * 20
                + [192, 5, 1, 42, 0, 0, 0]
                + [255, 7, 36, 0, 0, 0, 0, 0, 0])

    paths = []
    for i, (bssid, freq, _, dbm) in enumerate(APS):
        path = f"{iface}/BSSs/{i}"
        paths.append(path)
        wpa.AddObject(path, "fi.w1.wpa_supplicant1.BSS", {
            "BSSID": dbus.Array([dbus.Byte(int(x, 16)) for x in bssid.split(":")], signature="y"),
            "SSID": dbus.Array([dbus.Byte(c) for c in b"Office"], signature="y"),
            "Frequency": dbus.UInt16(freq),
            "Signal": dbus.Int16(dbm),
            "Age": dbus.UInt32(2),
            "IEs": dbus.Array([dbus.Byte(b) for b in ies(46 if i == 1 else 158)], signature="y"),
            # What a real supplicant adds: nested dictionaries holding string arrays, rates, flags.
            "Mode": "infrastructure",
            "Privacy": dbus.Boolean(True),
            "Rates": dbus.Array([dbus.UInt32(r) for r in (54000000, 48000000, 24000000, 6000000)], signature="u"),
            "WPA": dbus.Dictionary({"KeyMgmt": dbus.Array([], signature="s"), "Pairwise": dbus.Array([], signature="s"),
                                    "Group": ""}, signature="sv"),
            "RSN": dbus.Dictionary({"KeyMgmt": dbus.Array(["wpa-psk", "sae"], signature="s"),
                                    "Pairwise": dbus.Array(["ccmp"], signature="s"),
                                    "Group": "ccmp", "MgmtGroup": "aes128cmac"}, signature="sv"),
            "WPS": dbus.Dictionary({"Type": ""}, signature="sv"),
            "ANQP": dbus.Dictionary({}, signature="sv"),
        }, [])

    by_bssid = {bssid: paths[i] for i, (bssid, *_rest) in enumerate(APS)}
    roam = ("m = " + repr(by_bssid) + "; "
            "self.Set('fi.w1.wpa_supplicant1.Interface', 'CurrentBSS', dbus.ObjectPath(m[args[0]]))")
    wpa.AddObject(iface, "fi.w1.wpa_supplicant1.Interface", {
        "BSSs": dbus.Array([dbus.ObjectPath(p) for p in paths], signature="o"),
        "CurrentBSS": dbus.ObjectPath(paths[0]),
        "State": "completed",
    }, [
        ("SignalPoll", "", "a{sv}", "ret = {'rssi': dbus.Int32(-70, variant_level=1)}"),
        ("Roam", "s", "", roam),
    ])

if WITH_IWD:
    servers.append(DBusTestCase.spawn_server("net.connman.iwd", "/", "net.connman.iwd", system_bus=False, stdout=sys.stderr))
    # Started by D-Bus activation (as a GetInterface call does on a real system), knowing no interfaces.
    wpa_proc = DBusTestCase.spawn_server("fi.w1.wpa_supplicant1", "/fi/w1/wpa_supplicant1",
                                         "fi.w1.wpa_supplicant1", system_bus=False, stdout=sys.stderr)
    servers.append(wpa_proc)
    wpa = dbus.Interface(dbus.SessionBus().get_object("fi.w1.wpa_supplicant1", "/fi/w1/wpa_supplicant1"), dbusmock.MOCK_IFACE)
    wpa.AddMethod("fi.w1.wpa_supplicant1", "GetInterface", "s", "o",
                  "raise dbus.exceptions.DBusException('wpa_supplicant knows nothing about this interface.', "
                  "name='fi.w1.wpa_supplicant1.InterfaceUnknown')")

print("READY", flush=True)
while True:
    time.sleep(60)
