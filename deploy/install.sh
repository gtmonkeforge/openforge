#!/usr/bin/env bash
# Installs OpenForge as a systemd service. Run it as root from an unpacked Linux release:
#
#   sudo ./install.sh              installs to /opt/openforge and starts the service
#   sudo ./install.sh /srv/forge   installs somewhere else
#
# Upgrading is the same command: the server files are replaced, and .env, data/ and docs/ are kept.
set -euo pipefail

TARGET="${1:-/opt/openforge}"
SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE=/etc/systemd/system/openforge.service

[[ $EUID -eq 0 ]] || { echo "Run this as root (sudo ./install.sh)." >&2; exit 1; }
[[ -x "$SOURCE/OpenForge" ]] || { echo "Run this from an unpacked OpenForge Linux release (OpenForge not found next to it)." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemd is required." >&2; exit 1; }

id openforge &>/dev/null || useradd --system --home-dir "$TARGET" --shell /usr/sbin/nologin openforge
mkdir -p "$TARGET"

if systemctl is-active --quiet openforge; then
    echo "==> Stopping the running server"
    systemctl stop openforge
fi

echo "==> Copying the server to $TARGET"
# Keep the settings, the database and any docs edited on the server.
for item in "$SOURCE"/*; do
    name="$(basename "$item")"
    case "$name" in
        .env|data|install.sh) continue ;;
        docs) [[ -d "$TARGET/docs" ]] && continue ;;
    esac
    rm -rf "${TARGET:?}/$name"
    cp -a "$item" "$TARGET/"
done

touch "$TARGET/.env"
chmod 600 "$TARGET/.env"
chown -R openforge:openforge "$TARGET"

echo "==> Installing $SERVICE"
sed "s#/opt/openforge#$TARGET#g" "$SOURCE/openforge.service" > "$SERVICE"
systemctl daemon-reload
systemctl enable --now openforge

echo
echo "OpenForge is running. Logs: journalctl -u openforge -f"

if ! grep -q '^ROOT_ADMIN_ID=' "$TARGET/.env"; then
    sleep 3
    token="$(cat "$TARGET/data/setup-token" 2>/dev/null || true)"
    echo "Finish setting it up at http://localhost:5000/setup. It only listens on localhost until you"
    echo "change that, so from another machine use a tunnel first:  ssh -L 5000:localhost:5000 <this server>"
    [[ -n "$token" ]] && echo "Setup token: $token"
fi
