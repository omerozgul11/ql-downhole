#!/usr/bin/env bash
# Virtual display + window manager + VNC + noVNC, then Downhole Studio (restarted whenever it is closed).
set -euo pipefail
export DISPLAY=:1
mkdir -p "$HOME/.vnc" "$HOME/Downhole Studio" "$HOME/Test data" "$HOME/Uploads" "$HOME/.config"
# The home folder acts as "Documents", so projects and reports go to ~/Downhole Studio (shared with the file browser).
printf 'XDG_DOCUMENTS_DIR="$HOME"\n' > "$HOME/.config/user-dirs.dirs"
Xvfb :1 -screen 0 "${SCREEN:-1680x1050}x24" -nolisten tcp &
for _ in $(seq 50); do [ -e /tmp/.X11-unix/X1 ] && break; sleep 0.1; done
openbox &
x11vnc -storepasswd "$VNC_PASSWORD" "$HOME/.vnc/passwd" >/dev/null
x11vnc -display :1 -rfbauth "$HOME/.vnc/passwd" -forever -shared -noxdamage -quiet -rfbport 5900 &
websockify --web /usr/share/novnc 6080 localhost:5900 &
# Point the application at the test licence server.
cat > /opt/downhole-studio/appsettings.json <<JSON
{
  "LicenseServerUrl": "${LICENSE_SERVER_URL}",
  "PrivacyPolicyUrl": "https://quakelogic.net/privacy",
  "SupportUrl": "https://quakelogic.net/contact",
  "PurchaseUrl": "https://quakelogic.net/downhole-studio"
}
JSON
while true; do
  /opt/downhole-studio/DownholeStudio >>"$HOME/app-console.log" 2>&1 || true
  echo "$(date -Is) Downhole Studio exited; restarting in 2 s" >>"$HOME/app-console.log"
  sleep 2
done
