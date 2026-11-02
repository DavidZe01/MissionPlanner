#!/bin/bash
# Test "Mission Planner.app" on a macOS CI runner: startup smoke test and the
# screen tour on XQuartz's Xvfb, with PNG screenshots. Usage: ci-test.sh <app> <out-dir>
set -uo pipefail
app=$(cd -- "${1:?app}" && pwd)
out=$(mkdir -p -- "${2:?out}" && cd -- "$2" && pwd)
launcher="$app/Contents/MacOS/MissionPlanner"

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

/opt/X11/bin/Xvfb :99 -screen 0 1400x1000x24 -nolisten tcp > "$out/xvfb.log" 2>&1 &
xvfb=$!
export DISPLAY=:99
for _ in $(seq 1 50); do [[ -S /tmp/.X11-unix/X99 ]] && break; sleep 0.2; done

{
    echo "uname -m: $(uname -m)"; sw_vers
    echo "mono: $(lipo -archs "$(brew --prefix mono)/bin/mono")"
    file "$app/Contents/Frameworks/libgdiplus.0.dylib" "$app/Contents/Resources/app/libSkiaSharp.dylib"
    otool -L "$app/Contents/Frameworks/libgdiplus.0.dylib"
    ls -la "$app/Contents/Frameworks"
} > "$out/environment.txt" 2>&1

status=0
run_limited 150 "$launcher" --self-test > "$out/smoke.log" 2>&1
grep -qx LINUX_SMOKE_TEST_PASS "$out/smoke.log" && echo "SMOKE: PASS" || { echo "SMOKE: FAIL"; status=1; }
grep SMOKE_RUNTIME "$out/smoke.log" || true

mkdir -p -- "$out/tour"
TOUR_OUT="$out/tour" TOUR_XWD=/opt/X11/bin/xwd TOUR_CONNECT=${TOUR_CONNECT:-} \
    TOUR_SKIP=${TOUR_SKIP-ConfigParamLoading} \
    run_limited 1500 "$launcher" --tour > "$out/tour.log" 2>&1
grep -E '^TOUR_(RUNTIME|DONE)' "$out/tour.log" || { echo "TOUR: did not finish"; status=1; }
for f in "$out"/tour/*.xwd; do
    [[ -f $f ]] || continue
    xwdtopnm "$f" 2>/dev/null | pnmtopng > "${f%.xwd}.png" 2>/dev/null && rm -f -- "$f"
done
cp -R "$HOME/Library/Logs/MissionPlanner" "$out/logs" 2>/dev/null || true
cp "$app/Contents/Resources/app/BUILD-INFO.txt" "$out/" 2>/dev/null || true
kill "$xvfb" 2>/dev/null
exit $status
