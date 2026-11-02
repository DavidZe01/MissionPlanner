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
    ls -d /opt/X11/etc/fonts /opt/X11/lib/X11/fontconfig /opt/homebrew/etc/fonts 2>&1
} > "$out/environment.txt" 2>&1

status=0
part=${CI_TEST_PART:-all}

run_tour() { # name [connection]
    local dir="$out/$1" attempt
    mkdir -p -- "$dir"
    # a step that hangs ends the run (TOUR HANG); the next run resumes after it
    for attempt in 1 2 3 4 5 6; do
        TOUR_OUT="$dir" TOUR_XWD=/opt/X11/bin/xwd TOUR_CONNECT=${2:-} \
            TOUR_SKIP=${TOUR_SKIP-} \
            run_limited 1800 "$launcher" --tour >> "$dir.log" 2>&1
        grep -q '^TOUR_DONE' "$dir.log" && break
    done
    grep -E '^TOUR_DONE|^TOUR HANG|^TOUR connected' "$dir.log" || true
    grep -q '^TOUR_DONE' "$dir.log" || { echo "$1: did not finish"; status=1; }
    grep -E '^TOUR FAIL' "$dir.log" | cut -c1-200 || true
    for f in "$dir"/*.xwd; do
        [[ -f $f ]] || continue
        xwdtopnm "$f" 2>/dev/null | pnmtopng > "${f%.xwd}.png" 2>/dev/null && rm -f -- "$f"
    done
}

if [[ $part == offline || $part == all ]]; then
run_limited 150 "$launcher" --self-test > "$out/smoke.log" 2>&1
grep -qx LINUX_SMOKE_TEST_PASS "$out/smoke.log" && echo "SMOKE: PASS" || { echo "SMOKE: FAIL"; status=1; }
grep SMOKE_RUNTIME "$out/smoke.log" || true


run_tour tour
fi

if [[ $part == connected || $part == all ]]; then
# Same tour connected to the bundled native simulator (ArduCopter SITL).
sitl_dir="$app/Contents/Resources/sitl"
if [[ -x $sitl_dir/ArduCopter ]]; then
    file "$sitl_dir/ArduCopter" | tee -a "$out/environment.txt"
    simdir=$(mktemp -d)
    (cd "$simdir" && exec "$sitl_dir/ArduCopter" -Mquad -O-35.363261,149.165230,584,353 -s1 \
        --serial0 tcp:0 --defaults "$sitl_dir/copter.parm" > "$out/sitl.log" 2>&1) &
    sitl_pid=$!
    sleep 8
    if kill -0 "$sitl_pid" 2>/dev/null; then
        echo "SITL: running"
        run_tour tour-connected tcp:127.0.0.1:5760
    else
        echo "SITL: did not start"; tail -20 "$out/sitl.log"; status=1
    fi
    kill "$sitl_pid" 2>/dev/null
else
    echo "SITL: not bundled"
fi
fi
cp -R "$HOME/Library/Logs/MissionPlanner" "$out/logs" 2>/dev/null || true
cp "$app/Contents/Resources/app/BUILD-INFO.txt" "$out/" 2>/dev/null || true
kill "$xvfb" 2>/dev/null
exit $status
