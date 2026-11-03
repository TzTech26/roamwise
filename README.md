# Roamwise

Roamwise keeps your PC on the best Wi-Fi access point. It runs on Windows 10/11 and on Linux (Ubuntu, Linux Mint and other NetworkManager desktops).

Your computer tends to stick with the first access point it joined, even after you've walked across the building and three closer ones are in range. On busy networks like offices, campuses, apartment buildings and mesh systems, you end up with a weak, slow connection while a strong one sits next to you. Roamwise runs quietly in the notification area (or the top bar / panel on Linux), checks the access points of the network you're on, and moves you to a better one when it's clearly better.

<p align="center">
  <img src="docs/flyout.png" width="380" alt="The Roamwise panel: each access point of the network on one signal scale, with the current one highlighted and a better one selected">
</p>

## What it does

- **Moves you to a better access point.** It ranks every access point broadcasting your network and switches when another one is clearly better. It keeps your current network and uses the saved password Windows already has.
- **Ranks for speed, not just bars.** It starts from signal strength and adds points for 5 GHz and 6 GHz, wider channels and Wi-Fi 6/7. It takes points off busy access points, using the load the AP reports in its beacons or, if it doesn't report one, the number of other radios sharing its channel. The speed bonuses fade out as signal weakens, so a weak 6 GHz link never beats a solid 5 GHz one.
- **Doesn't flap.** A better access point has to stay better for several checks in a row, has to beat yours by a margin you choose, and Roamwise waits between switches. If a switch fails, that access point is skipped for 15 minutes and Windows reconnects you as usual.
- **Shows its reasoning.** The panel draws every access point on one signal scale, with your rules marked on each bar, and says in a sentence why it's staying or switching. Sort by band (grouped by 6, 5 and 2.4 GHz, then channel), by device, or best first. Select an access point to see its signal, channel, standard, load, hardware and rating breakdown, or switch to it yourself.
- **Knows which radio belongs to which access point.** Radios are grouped into physical devices. Names come from, in order: names you give (pencil button in the details), your controller or a list you import (Settings › Names), the name an AP announces (Cisco, Aruba, Aerohive, MikroTik, Meraki, Mist, or the WPS device name), and otherwise the end of the device MAC, like "AP 27:63".
- **Shows 6 GHz radios your PC can't quite hear.** Access points list their 6 GHz siblings in a neighbor report on 2.4 and 5 GHz. Roamwise shows those too, marked as out of earshot, instead of letting them disappear. They aren't picked automatically until Windows can measure their signal.
- **Pulls names from your controller.** Ubiquiti UniFi, TP-Link Omada, Cisco Meraki, Juniper Mist and Ruckus SmartZone are read with read-only credentials you create; keys are encrypted with Windows DPAPI for your account. For anything else (Aruba, eero, Netgear, OpenWrt, Ruckus Unleashed…) import a CSV or text file with a MAC address and a name per line.
- **Doesn't interrupt you.** A switch drops the connection for a moment, so Roamwise holds off while you're streaming, downloading or on a call (unless your signal is very weak). Pause it for an hour from the tray menu or Settings.
- **Updates itself.** Once a day it checks this repository's GitHub releases. Install from the panel or Settings › About, or let it install on its own. Downloads come only from GitHub and must match the release's SHA256SUMS.txt.
- **Low-latency mode (optional).** Pauses the background scans Windows runs about once a minute, which cause lag spikes in games and calls. Roamwise turns it back off when it closes.

## Settings

Everything is adjustable from **Settings** (the gear in the panel, or right-click the tray icon).

| Setting | Default | What it means |
| --- | --- | --- |
| Check every | 30 s | How often Roamwise scans. Doubled on battery if you want. |
| Required improvement | +8 dB | How much better another access point must score. |
| Weakest access point to move to | −72 dBm | Never switch to anything weaker than this. |
| Confirm over | 2 checks | The better access point must stay better this many checks in a row. |
| Wait between switches | 2 min | Minimum time between two switches. |
| Only switch when my signal is weak | Off | Leave a good connection alone; look around only below a threshold (default −67 dBm). |
| 5 GHz / 6 GHz bonus | +6 / +8 dB | How much faster bands count for. |
| Avoid 2.4 GHz | Off | Counts 2.4 GHz access points 6 points lower. |
| Account for busy access points | On | Penalize access points with crowded airtime. |
| Wait while I'm using the network | On, over 2 Mbps | Hold off switching during calls, streams and downloads. |
| Which networks | All, except excluded | Or switch to "Only networks I list". Saved networks can be added with one click. |
| Get names from | None | UniFi, Omada, Meraki, Mist, Ruckus, or an imported list. |
| Updates | Check daily | Optionally install them automatically. |

<p align="center">
  <img src="docs/settings.png" width="720" alt="The Switching settings page with a plain-language summary of the rules">
</p>

## Install

1. Download `Roamwise-win-x64.zip` (or `win-arm64`) from [Releases](https://github.com/TzTech26/roamwise/releases) and unzip it anywhere.
2. Run `Roamwise.exe`. It doesn't need an installer, admin rights, or a separate .NET install.
3. Turn on **Start with Windows** in Settings if you want it to run at sign-in.

### Linux (Ubuntu, Linux Mint)

1. Download `Roamwise-linux-x64.AppImage` (or `linux-arm64`) from [Releases](https://github.com/TzTech26/roamwise/releases).
2. Make it executable and run it:

   ```bash
   chmod +x Roamwise-linux-x64.AppImage && ./Roamwise-linux-x64.AppImage
   ```

   The first run adds Roamwise to your app menu. Keep the AppImage somewhere permanent (like `~/Applications`) so the menu entry, Start when I log in and self-update keep working.
3. Click **Allow Roamwise to switch** in the panel and enter your password once. Until then Roamwise shows what it would do but doesn't switch.

**Why the one-time setup:** NetworkManager lets any app read the access points, but choosing a specific one goes through `wpa_supplicant`, which only root may talk to. The setup installs one D-Bus policy file, `/etc/dbus-1/system.d/roamwise-<you>.conf`, that lets your user (no one else) ask `wpa_supplicant` to roam. Remove it any time from Settings › Roaming › Permission to switch. Without it, signal is NetworkManager's estimate and Wi-Fi standard and load are unknown.

**Tray icon:** Linux Mint (Cinnamon), KDE and XFCE show it out of the box. On Ubuntu it needs the AppIndicator extension, which Ubuntu ships turned on. On other GNOME desktops install "AppIndicator and KStatusNotifierItem Support".

**Requirements:** NetworkManager (the default on Ubuntu and Mint). If the AppImage won't start and mentions FUSE, install `libfuse2` (Ubuntu 22.04, Mint 21) or `libfuse2t64` (Ubuntu 24.04, Mint 22). Controller keys are kept in your login keyring.

### Windows location access

Windows treats nearby access points as location data, so it only shares them with apps when **Settings › Privacy & security › Location › Let desktop apps access your location** is on. Roamwise tells you if it's off and links straight to the setting. It never looks up or stores your location.

## How switching works

On Windows, Roamwise calls the Native Wifi API (`WlanConnect`) with your existing profile and a desired BSSID list containing only the target access point. On Linux, it asks `wpa_supplicant` to `Roam` to the target BSSID within the connection NetworkManager already has. Either way the system does the actual association, including authentication, so nothing about your network's security changes. Expect a brief reconnect during a switch, which is why Roamwise only switches when the gain is worth it.

## Releasing

Push a tag like `v1.2.0`. The workflow builds Windows and Linux for x64 and ARM64, stamps the version from the tag, tests the Linux Wi-Fi code against mock NetworkManager and wpa_supplicant services, and publishes a release with the zips, the AppImages and `SHA256SUMS.txt`. Running copies pick it up within a day.

```bash
git tag v1.2.0 && git push origin v1.2.0
```

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
```

```bash
dotnet publish src/Roamwise/Roamwise.csproj -c Release -r win-x64 -o artifacts/win-x64
```

On Linux, build the AppImage (downloads `appimagetool` the first time):

```bash
tools/build-appimage.sh linux-x64
```

To run the Linux Wi-Fi tests (needs `python3-dbusmock` and `dbus-x11`):

```bash
tests/linux/run.sh out/linux-x64/roamwise
```

Useful switches for `Roamwise.exe` and the Linux `roamwise` / AppImage:

- `--background` starts in the tray without opening the panel (used by Start with Windows / Start when I log in).
- `--settings [general|roaming|ranking|networks|names|activity|about]` opens Settings on a page.
- `--verbose` writes a trace of every check to `debug.log` in the settings folder.
- `--diagnose report.txt` writes everything Roamwise sees (adapters, connection, every access point with its score) and exits. Attach it to bug reports.
- `--demo` shows the UI with a made-up busy office network.

Settings and the activity history live in `%APPDATA%\Roamwise` on Windows and `~/.config/Roamwise` on Linux.

## Project layout

```
src/Roamwise.Core/           Shared by both apps
  Core/RoamEngine.cs         The check-rank-decide-switch loop
  Core/ApScorer.cs           How access points are scored
  Core/AppSettings.cs        Settings model and persistence
  Core/NameSources.cs        Access point names from controllers and imported lists
  Core/Updater.cs            Self-update from GitHub Releases
  Wifi/IWifiBackend.cs       What each platform's Wi-Fi layer provides
  Wifi/InfoElements.cs       802.11 element parsing
src/Roamwise/                Windows app (WPF)
  Native/WlanNative.cs       P/Invoke declarations for wlanapi.dll
  Wifi/WlanClient.cs         Native Wifi backend
  UI/                        Tray icon, flyout, signal chart, settings window
src/Roamwise.Linux/          Linux app (Avalonia)
  Platform/LinuxWifi.cs      NetworkManager + wpa_supplicant backend over D-Bus
  Platform/SwitchPermission.cs  The one-time D-Bus policy for switching
  Platform/Desktop.cs        Autostart, app menu, notifications, single instance
  Ui/                        Tray icon, panel, signal chart, settings window
tests/linux/                 Mock NetworkManager/wpa_supplicant tests
tools/make-icon.ps1          Regenerates the app icon
tools/build-appimage.sh      Builds the Linux AppImage
```

## License

MIT. See [LICENSE](LICENSE).
