#!/usr/bin/env bash
# Runs the Linux build against mock NetworkManager and wpa_supplicant services (python3-dbusmock) on a private bus.
# Usage: tests/linux/run.sh path/to/roamwise
set -euo pipefail
bin="$(readlink -f "$1")"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
export HOME="$work"   # keep settings and logs out of the runner's home

eval "$(dbus-launch --sh-syntax)"
export ROAMWISE_SYSTEM_BUS="$DBUS_SESSION_BUS_ADDRESS"
trap 'kill $mock 2>/dev/null || true; kill $DBUS_SESSION_BUS_PID 2>/dev/null || true' EXIT

start_mock() {
  python3 "$here/mock_wifi.py" "$1" > "$work/mock.out" &
  mock=$!
  for _ in $(seq 50); do grep -q READY "$work/mock.out" 2>/dev/null && return; sleep 0.2; done
  echo "mock services didn't start"; exit 1
}

check() { grep -q -- "$2" "$1" || { echo "FAIL: expected '$2' in $1"; cat "$1"; exit 1; }; }

echo "== NetworkManager only (before the one-time setup)"
start_mock nm
timeout 60 "$bin" --diagnose "$work/nm.txt"
cat "$work/nm.txt"
check "$work/nm.txt" "Can switch access points: False"
check "$work/nm.txt" "Connection: profile 'Office', ssid 'Office', bssid 3c:28:6d:a4:f1:20"
check "$work/nm.txt" "Access points: 3"
check "$work/nm.txt" "3c:28:6d:b2:07:9c   -52 dBm  5 GHz    ch 149"
check "$work/nm.txt" "Saved networks: Office, Home"
timeout 60 "$bin" --engine-test | tee "$work/engine-nm.txt"
check "$work/engine-nm.txt" "state Connected, 3 radios"
kill $mock; wait $mock 2>/dev/null || true

echo "== With wpa_supplicant"
start_mock nm+wpa
timeout 60 "$bin" --diagnose "$work/wpa.txt"
cat "$work/wpa.txt"
check "$work/wpa.txt" "Can switch access points: True"
check "$work/wpa.txt" "rssi -70"
check "$work/wpa.txt" "3c:28:6d:b2:07:9c   -54 dBm  5 GHz    ch 149  80 MHz  Wi-Fi 6      load  18%  sta   9"
timeout 60 "$bin" --roam-test 3c:28:6d:b2:07:9c | tee "$work/roam.txt"
check "$work/roam.txt" "roam-test: now on 3c:28:6d:b2:07:9c"
timeout 60 "$bin" --engine-test | tee "$work/engine-wpa.txt"
check "$work/engine-wpa.txt" "state Connected, 3 radios"

kill $mock; wait $mock 2>/dev/null || true

echo "== NetworkManager on iwd"
start_mock nm+iwd
timeout 60 "$bin" --diagnose "$work/iwd.txt"
cat "$work/iwd.txt"
check "$work/iwd.txt" "Can switch access points: False"
check "$work/iwd.txt" "iwd: in use"
check "$work/iwd.txt" "Unsupported: NetworkManager runs Wi-Fi through iwd"
check "$work/iwd.txt" "Access points: 2"
if grep -q "00:01:02:00:00:01.*ch 2 " "$work/iwd.txt"; then echo "FAIL: iwd placeholder kept its made-up channel"; exit 1; fi
timeout 60 "$bin" --engine-test | tee "$work/engine-iwd.txt"
check "$work/engine-iwd.txt" "state Connected, 1 radios"
check "$work/engine-iwd.txt" "can only see the access point you're on for Office"

echo "All Linux D-Bus checks passed."
