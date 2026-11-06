#!/usr/bin/env bash
# Virtual display + window manager + VNC + noVNC, then Downhole Studio (restarted whenever it is closed).
set -euo pipefail
export DISPLAY=:1
mkdir -p "$HOME/.vnc" "$HOME/Downhole Studio" "$HOME/Test data" "$HOME/Uploads" "$HOME/.config"
# Projects and reports go to Documents/Downhole Studio, which is the folder shared with the file browser.
mkdir -p "$HOME/Documents"
ln -sfn "$HOME/Downhole Studio" "$HOME/Documents/Downhole Studio"
printf 'XDG_DOCUMENTS_DIR="$HOME/Documents"\n' > "$HOME/.config/user-dirs.dirs"
# Shortcuts in the file dialogs.
mkdir -p "$HOME/.config/gtk-3.0"
printf 'file://%s/Test%%20data Test data\nfile://%s/Uploads Uploads\nfile://%s/Downhole%%20Studio Downhole Studio\n' "$HOME" "$HOME" "$HOME" > "$HOME/.config/gtk-3.0/bookmarks"
# A restarted container keeps /tmp, and a leftover lock would stop the display from starting.
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1
Xvfb :1 -screen 0 "${SCREEN:-1680x1050}x24" -nolisten tcp &
XVFB_PID=$!
for _ in $(seq 50); do [ -e /tmp/.X11-unix/X1 ] && break; sleep 0.1; done
openbox &
x11vnc -storepasswd "$VNC_PASSWORD" "$HOME/.vnc/passwd" >/dev/null
x11vnc -display :1 -rfbauth "$HOME/.vnc/passwd" -forever -shared -noxdamage -quiet -rfbport 5900 &
VNC_PID=$!
websockify --web /usr/share/novnc 6080 localhost:5900 &
WEB_PID=$!
# Point the application at the test licence server.
cat > /opt/downhole-studio/appsettings.json <<JSON
{
  "LicenseServerUrl": "${LICENSE_SERVER_URL}",
  "PrivacyPolicyUrl": "https://quakelogic.net/privacy",
  "SupportUrl": "https://quakelogic.net/contact",
  "PurchaseUrl": "https://quakelogic.net/downhole-studio"
}
JSON
LOG="$HOME/app-console.log"
# File dialogs start in the working directory: use the folder the file browser shows, so reports can be downloaded there.
cd "$HOME/Downhole Studio"
(
  while true; do
    # Keep the log from growing without limit.
    if [ "$(stat -c %s "$LOG" 2>/dev/null || echo 0)" -gt 10000000 ]; then
      tail -c 1000000 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
    fi
    /opt/downhole-studio/DownholeStudio >>"$LOG" 2>&1 || true
    echo "$(date -Is) Downhole Studio exited; restarting in 2 s" >>"$LOG"
    sleep 2
  done
) &
# Nothing works without the display, VNC and the web bridge: if one stops, exit so Docker restarts the whole container.
wait -n "$XVFB_PID" "$VNC_PID" "$WEB_PID" || true
echo "$(date -Is) The display, VNC or web bridge stopped; restarting the container" >>"$LOG"
exit 1
