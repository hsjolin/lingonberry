#!/bin/sh
# Lingonberry VPN gateway - one container per customer app, running with
# network_mode: host so the WireGuard interface lives in the host netns.
#
# Expects:
#   IFACE    - interface name, e.g. "wg-3f2a1b9c" (must match config file name)
#   /config  - directory containing $IFACE.conf, written by proxy-app
set -e

IFACE="${IFACE:?IFACE env var is required}"
CONFIG="/config/${IFACE}.conf"

# Clean up any interface left behind by a previous (SIGKILLed) run.
if ip link show "$IFACE" >/dev/null 2>&1; then
    wg-quick down "$CONFIG"
fi

# RPi <-> RPi traffic within the VPN is forwarded through this gateway.
sysctl -w net.ipv4.ip_forward=1

# Customer isolation: allow forwarding inside this VPN, block this VPN
# from reaching (or being reached by) other Lingonberry VPNs.
iptables -C FORWARD -i "$IFACE" -o "$IFACE" -j ACCEPT 2>/dev/null ||
    iptables -I FORWARD 1 -i "$IFACE" -o "$IFACE" -j ACCEPT
for other in $(ip -o link show | cut -d: -f2 | cut -d@ -f1 | tr -d ' ' | grep '^wg-' || true); do
    if [ "$other" != "$IFACE" ]; then
        iptables -C FORWARD -i "$IFACE" -o "$other" -j DROP 2>/dev/null ||
            iptables -I FORWARD 1 -i "$IFACE" -o "$other" -j DROP
        iptables -C FORWARD -i "$other" -o "$IFACE" -j DROP 2>/dev/null ||
            iptables -I FORWARD 1 -i "$other" -o "$IFACE" -j DROP
    fi
done

shutdown() {
    wg-quick down "$CONFIG"
    # Remove the rules this gateway added.
    iptables -D FORWARD -i "$IFACE" -o "$IFACE" -j ACCEPT 2>/dev/null || true
    for other in $(ip -o link show | cut -d: -f2 | cut -d@ -f1 | tr -d ' ' | grep '^wg-' || true); do
        if [ "$other" != "$IFACE" ]; then
            iptables -D FORWARD -i "$IFACE" -o "$other" -j DROP 2>/dev/null || true
            iptables -D FORWARD -i "$other" -o "$IFACE" -j DROP 2>/dev/null || true
        fi
    done
}
trap shutdown TERM INT

wg-quick up "$CONFIG"

# Keep the container running; proxy-app restarts it to apply config changes.
# sleep runs as a background child so that the TERM trap (and thus
# wg-quick down) executes immediately instead of after the sleep ends.
while :; do sleep 1 & wait $!; done
