#!/usr/bin/env python3
"""Mock NetworkManager and (optionally) wpa_supplicant on the bus in DBUS_SESSION_BUS_ADDRESS, for CI.

    mock_wifi.py nm            start NetworkManager with one Wi-Fi device on "Office", three access points
    mock_wifi.py nm+wpa        the same plus wpa_supplicant (dBm, beacon data, Roam)

Prints READY once everything is registered, then serves until killed.
"""
import sys
import time

import dbus
import dbusmock
from dbusmock import DBusTestCase

WITH_WPA = len(sys.argv) > 1 and "wpa" in sys.argv[1]

# BSSID, frequency (MHz), NetworkManager strength (%), supplicant signal (dBm)
APS = [
    ("3c:28:6d:a4:f1:20", 5220, 45, -71),   # connected, 5 GHz ch 44
    ("3c:28:6d:b2:07:9c", 5745, 80, -54),   # better, 5 GHz ch 149
    ("3c:28:6d:a4:f1:21", 2437, 55, -66),   # 2.4 GHz ch 6
]

nm_proc, nm_obj = DBusTestCase.spawn_server_template("networkmanager", {}, stdout=sys.stderr, system_bus=False)
nm = dbus.Interface(nm_obj, dbusmock.MOCK_IFACE)
device = nm.AddWiFiDevice("mock_wifi0", "wlan0", 100)
aps = [nm.AddAccessPoint(device, f"ap{i}", "Office", bssid, 2, freq, 54000, strength, 0x100)
       for i, (bssid, freq, strength, _) in enumerate(APS)]
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

if WITH_WPA:
    wpa_proc, wpa_obj = DBusTestCase.spawn_server("fi.w1.wpa_supplicant1", "/fi/w1/wpa_supplicant1",
                                                  "fi.w1.wpa_supplicant1", system_bus=False, stdout=sys.stderr)
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

print("READY", flush=True)
while True:
    time.sleep(60)
