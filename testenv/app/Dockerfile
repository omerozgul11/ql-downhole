# Downhole Studio (Linux build) on a virtual display, served to a web browser with noVNC.
# Built by testenv/start.sh from artifacts/publish/linux-x64. For local testing only.
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb x11vnc novnc websockify openbox xdotool \
        libgtk-3-0t64 libice6 libsm6 libfontconfig1 libicu74 ca-certificates \
        fonts-dejavu-core fonts-liberation dbus-x11 xdg-utils \
    && rm -rf /var/lib/apt/lists/*
# Run as the same uid as the host user so test files can be inspected and removed from the host.
RUN userdel -r ubuntu 2>/dev/null || true; useradd -m -u 1000 -s /bin/bash tester \
    && printf 'd3f1c0de5a1e4b7c9d2a6f8e0b4c7a11\n' > /etc/machine-id
COPY --chown=tester:tester app/ /opt/downhole-studio/
COPY testenv/app/openbox-rc.xml /etc/xdg/openbox/rc.xml
COPY testenv/app/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /opt/downhole-studio/DownholeStudio \
    && ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html
USER tester
WORKDIR /home/tester
EXPOSE 6080
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
