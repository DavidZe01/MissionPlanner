#!/usr/bin/env bash
# ArduPilot's simulators (SITL) for macOS on Apple Silicon, for every vehicle and channel
# Mission Planner's Simulation screen offers (as ArduPilot publishes them for Windows):
#   stable  ArduCopter-stable (ArduCopter, ArduHeli), ArduPlane-stable, APMrover2-stable
#   beta    ArduCopter-beta, ArduPlane-beta, APMrover2-beta
#   dev     master
# (ArduPilot's own tags for each vehicle's current stable and beta.) Only the ones whose commit
# changed since the previous index are built, each commit once for all its vehicles.
#
# Usage: macOS/native/build-sitl.sh <output dir> [previous index.json] [base download URL]
# Writes the new binaries (<Vehicle>-<channel>), copter.parm and index.json to <output dir>;
# index.json lists every binary (new or kept from the previous index) with its commit and
# sha256, and the URL it is downloaded from (base URL + name). GCSViews/SITL.cs reads it.
set -euo pipefail
out=${1:?output directory}
previous=${2:-}
base_url=${3:-https://github.com/DavidZe01/MissionPlanner/releases/download/sitl-macos/}
repo=https://github.com/ArduPilot/ardupilot
work=${MP_SITL_WORK:-$HOME/Library/Caches/MissionPlanner-native/ardupilot-sitl}
mkdir -p "$out" "$work"
out=$(cd -- "$out" && pwd)

# vehicle waf-target binary ref channel
builds="ArduCopter copter arducopter ArduCopter-stable stable
ArduHeli heli arducopter-heli ArduCopter-stable stable
ArduPlane plane arduplane ArduPlane-stable stable
ArduRover rover ardurover APMrover2-stable stable
ArduCopter copter arducopter ArduCopter-beta beta
ArduHeli heli arducopter-heli ArduCopter-beta beta
ArduPlane plane arduplane ArduPlane-beta beta
ArduRover rover ardurover APMrover2-beta beta
ArduCopter copter arducopter master dev
ArduHeli heli arducopter-heli master dev
ArduPlane plane arduplane master dev
ArduRover rover ardurover master dev"

# each ref's commit (a tag's own commit, not the tag object)
commit_of() {
    local ref=$1 line
    if [[ $ref == master ]]; then
        git ls-remote "$repo" refs/heads/master | cut -f1
    else
        line=$(git ls-remote "$repo" "refs/tags/$ref^{}" | cut -f1)
        [[ -n $line ]] || line=$(git ls-remote "$repo" "refs/tags/$ref" | cut -f1)
        echo "$line"
    fi
}

# the previous index's commit for a vehicle and channel
previous_commit() {
    [[ -n $previous && -f $previous ]] || return 0
    python3 - "$previous" "$1" "$2" <<'EOF'
import json, sys
for b in json.load(open(sys.argv[1])).get("binaries", []):
    if b["vehicle"] == sys.argv[2] and b["channel"] == sys.argv[3]:
        print(b["commit"])
EOF
}

entries="$out/entries.txt"
: > "$entries"
for ref in $(echo "$builds" | awk '{print $4}' | sort -u); do
    commit=$(commit_of "$ref")
    [[ -n $commit ]] || { echo "no commit for $ref" >&2; exit 1; }
    echo "$ref: $commit"
    while read -r vehicle target binary bref channel; do
        [[ $bref == "$ref" ]] || continue
        if [[ $(previous_commit "$vehicle" "$channel") == "$commit" ]]; then
            echo "  $vehicle-$channel: unchanged, kept"
            echo "$vehicle $channel $ref $commit keep" >> "$entries"
        else
            echo "$vehicle $channel $ref $commit build $target $binary" >> "$entries"
        fi
    done <<< "$builds"
done

commits_to_build=$(awk '$5 == "build" {print $4}' "$entries" | sort -u)
if [[ -n $commits_to_build ]]; then
    # one clone kept between runs, checked out at each commit in turn
    src="$work/ardupilot"
    if [[ ! -d $src/.git ]]; then
        git clone -q "$repo" "$src"
    fi
    if [[ ! -x $work/venv/bin/python ]]; then
        python3 -m venv "$work/venv"
        "$work/venv/bin/pip" install -q empy==3.3.4 pexpect future ptyprocess lxml
    fi
    export PATH="$work/venv/bin:$PATH"
    # ArduPilot links macOS builds with -ld_classic, which newer Xcode no longer has: its new
    # linker then rejects the unaligned pointers of the packed AP_FWVersion ("pointer not
    # aligned"), which it only accepts without chained fixups
    if [[ $(uname -s) == Darwin ]]; then
        export LINKFLAGS="${LINKFLAGS:-} -Wl,-no_fixup_chains"
    fi
    for commit in $commits_to_build; do
        refs=$(awk -v c="$commit" '$4 == c {print $3}' "$entries" | sort -u | tr '\n' ' ')
        targets=$(awk -v c="$commit" '$4 == c && $5 == "build" {print $6}' "$entries" | sort -u | tr '\n' ' ')
        echo "=== $commit ($refs): $targets"
        (
            cd "$src"
            for ref in $refs; do
                if [[ $ref == master ]]; then
                    git fetch -q --force origin master
                else
                    git fetch -q --force origin "+refs/tags/$ref:refs/tags/$ref"
                fi
            done
            git checkout -q -f "$commit"
            git clean -q -fdx -e build
            git submodule -q sync --recursive
            git submodule -q update --init --recursive --force
            log="$work/waf-${commit:0:10}"
            ./waf configure --board sitl > "$log-configure.log" 2>&1 || { tail -40 "$log-configure.log" >&2; exit 1; }
            # shellcheck disable=SC2086
            ./waf $targets > "$log-build.log" 2>&1 || { tail -40 "$log-build.log" >&2; exit 1; }
            version=$(git describe --tags --always)
            awk -v c="$commit" '$4 == c && $5 == "build" {print $1, $2, $7}' "$entries" | while read -r vehicle channel binary; do
                cp "build/sitl/bin/$binary" "$out/$vehicle-$channel"
                chmod 755 "$out/$vehicle-$channel"
                echo "$version" > "$out/$vehicle-$channel.version"
            done
            # the stable copter's default parameters (the bundled simulators' copter.parm)
            if [[ " $refs" == *" ArduCopter-stable "* ]]; then
                cp Tools/autotest/default_params/copter.parm "$out/copter.parm"
            fi
        )
    done
fi

# the index: every vehicle and channel, built now or kept from the previous index
python3 - "$entries" "$out" "$base_url" "${previous:-}" <<'EOF'
import hashlib, json, os, sys, time
entries, out, base, previous = sys.argv[1:5]
old = {}
if previous and os.path.exists(previous):
    for b in json.load(open(previous)).get("binaries", []):
        old[(b["vehicle"], b["channel"])] = b
binaries = []
for line in open(entries):
    f = line.split()
    vehicle, channel, ref, commit, action = f[:5]
    name = vehicle + "-" + channel
    if action == "keep":
        binaries.append(old[(vehicle, channel)])
        continue
    path = os.path.join(out, name)
    data = open(path, "rb").read()
    version = open(path + ".version").read().strip()
    os.remove(path + ".version")
    binaries.append({"vehicle": vehicle, "channel": channel, "ref": ref, "commit": commit,
                     "version": version, "sha256": hashlib.sha256(data).hexdigest(),
                     "size": len(data), "url": base + name})
json.dump({"updated": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
           "platform": "macos-arm64", "binaries": binaries},
          open(os.path.join(out, "index.json"), "w"), indent=1)
os.remove(entries)
for b in binaries:
    print("%-11s %-6s %-17s %s %s" % (b["vehicle"], b["channel"], b["ref"], b["commit"][:10], b["version"]))
EOF
