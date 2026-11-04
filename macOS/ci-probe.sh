#!/bin/bash
# Quick headless checks on a macOS runner (see Probe.cs), plus the app on the
# real XQuartz server (GLX). Usage: ci-probe.sh <app> <out-dir>
set -uo pipefail
app=$(cd -- "${1:?app}" && pwd)
out=$(mkdir -p -- "${2:?out}" && cd -- "$2" && pwd)
launcher="$app/Contents/MacOS/MissionPlanner"
status=0

run_limited() { # seconds command...
    local limit=$1; shift
    "$@" &
    local pid=$! elapsed=0
    while kill -0 "$pid" 2>/dev/null; do
        if (( elapsed >= limit )); then
            kill -QUIT "$pid" 2>/dev/null; sleep 5; kill -KILL "$pid" 2>/dev/null
            wait "$pid" 2>/dev/null; return 124
        fi
        sleep 1; elapsed=$((elapsed + 1))
    done
    wait "$pid"
}

# headless: a virtual display is enough for the launcher
/opt/X11/bin/Xvfb :98 -screen 0 1024x768x24 -nolisten tcp > /dev/null 2>&1 &
xvfb=$!
sleep 2
DISPLAY=:98 run_limited 120 "$launcher" --probe gstreamer > "$out/probe-gstreamer.log" 2>&1
grep -E '^PROBE|frames:' "$out/probe-gstreamer.log" || true

sitl_dir="$app/Contents/Resources/sitl"
simdir=$(mktemp -d)
echo "mission planner probe" > "$simdir/mp-probe.txt"
(cd "$simdir" && exec "$sitl_dir/ArduCopter" -Mquad -O-35.363261,149.165230,584,353 -s1 \
    --serial0 tcp:0 --defaults "$sitl_dir/copter.parm" > "$out/sitl.log" 2>&1) &
sitl=$!
sleep 8
DISPLAY=:98 run_limited 400 "$launcher" --probe vehicle 127.0.0.1:5760 > "$out/probe-vehicle.log" 2>&1
grep -E '^PROBE|^/:|^serving|^(OPTIONS|PROPFIND|GET) |^mounted' "$out/probe-vehicle.log" || true
kill "$sitl" 2>/dev/null

# firmware board detection from USB ids (no hardware needed)
DISPLAY=:98 run_limited 120 "$launcher" --probe boards > "$out/probe-boards.log" 2>&1
grep -E '^PROBE|->' "$out/probe-boards.log" || true
kill "$xvfb" 2>/dev/null

# the real XQuartz server, as on a user's Mac
open -a XQuartz || true
for _ in $(seq 1 60); do [[ -S /tmp/.X11-unix/X0 ]] && break; sleep 1; done
ls -la /tmp/.X11-unix/ > "$out/x11-sockets.txt" 2>&1
if [[ -S /tmp/.X11-unix/X0 ]]; then
    DISPLAY=:0 /opt/X11/bin/glxinfo -B > "$out/glxinfo.txt" 2>&1 || true
    grep -E 'direct rendering|OpenGL (vendor|renderer|version)' "$out/glxinfo.txt" || head -5 "$out/glxinfo.txt"
    DISPLAY=:0 run_limited 180 "$launcher" --self-test > "$out/smoke-xquartz.log" 2>&1
    grep -E 'LINUX_SMOKE_TEST_PASS|SMOKE_RUNTIME|GLX|gl True|gl False' "$out/smoke-xquartz.log" | sort | uniq -c | head
    screencapture -x "$out/xquartz-screen.png" 2>/dev/null || true
else
    echo "XQuartz did not start"
fi

# Bluetooth and camera as macOS sees them when the app is opened from Finder: it reads the
# usage descriptions from the Info.plist of the app the process belongs to, and ends a
# process that has none. Compare the app (Mono runs in mono-host, inside the bundle) with a
# copy whose launcher has to exec Homebrew's mono.
nohost="$(dirname -- "$app")/Mission Planner mono.app"
rm -rf -- "$nohost" && cp -R "$app" "$nohost" && rm -f -- "$nohost/Contents/MacOS/mono-host"
codesign --force --sign - "$nohost" > /dev/null 2>&1 || true
logs="$HOME/Library/Logs/MissionPlanner"
privacy_probe() { # label app probe args...
    local label=$1 bundle=$2 name=$3
    shift 2
    rm -f -- "$logs/probe-$name.log"
    run_limited 120 /usr/bin/open -W -n -a "$bundle" --args --probe "$@" > "$out/open-$name-$label.txt" 2>&1
    cp -- "$logs/probe-$name.log" "$out/probe-$name-$label.log" 2>/dev/null || echo "(no output)" > "$out/probe-$name-$label.log"
    echo "$name opened as an app, $label: $(grep -E '^(probe |PROBE |ble|frames)' "$out/probe-$name-$label.log" | tr '\n' ' ' | cut -c1-300)"
}
camera="avfvideosrc device-index=0 ! videoconvert ! video/x-raw,format=BGRx ! appsink name=outsink"
privacy_probe host "$app" ble
privacy_probe mono "$nohost" ble
privacy_probe host "$app" gstreamer "$camera"
privacy_probe mono "$nohost" gstreamer "$camera"
ls -la "$HOME/Library/Logs/DiagnosticReports/" > "$out/crash-reports.txt" 2>&1
for f in "$HOME"/Library/Logs/DiagnosticReports/*; do
    [[ -f $f ]] && grep -q -i -E 'usage description|privacy-sensitive' "$f" && { echo "== $f"; grep -i -E -m4 'usage description|privacy-sensitive|"procName"|Process:' "$f"; }
done > "$out/privacy-crashes.txt" 2>&1
head -20 "$out/privacy-crashes.txt"
/usr/bin/log show --last 15m --style compact \
    --predicate 'process == "tccd" OR eventMessage CONTAINS[c] "usage description"' > "$out/tcc.log" 2>&1 || true
grep -i -E 'mono|Mission Planner|org.ardupilot|usage description' "$out/tcc.log" | tail -25 || true
exit $status
