#!/usr/bin/env bash
# Install the newest native Mission Planner build (macos-native.yml) into /Applications.
#
# A browser download gets the quarantine attribute, and Gatekeeper then refuses an app
# without a Developer ID signature; the GitHub CLI downloads without it, so the app opens
# from Finder straight away. Needs the GitHub CLI, logged in once:
#   brew install gh && gh auth login
# Then, in Terminal:
#   curl -fsSL https://raw.githubusercontent.com/DavidZe01/MissionPlanner/macos-arm64/macOS/native/install.sh | bash
# Environment: MP_REPO (owner/repo), MP_RUN (a run id instead of the newest successful
# one), MP_ZIP (a MissionPlanner-macos-native-arm64.zip already on this Mac, e.g. from
# macOS/native/build.sh, instead of downloading one), MP_APPLICATIONS (where to install,
# default /Applications), MP_NO_OPEN=1.
set -euo pipefail
repo=${MP_REPO:-DavidZe01/MissionPlanner}
applications=${MP_APPLICATIONS:-/Applications}
artifact=MissionPlanner-macos-native-arm64

[[ $(uname -m) == arm64 ]] || echo "Note: this build is for Apple Silicon (arm64)." >&2

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
if [[ -n ${MP_ZIP:-} ]]; then
    [[ -f $MP_ZIP ]] || { echo "No such file: $MP_ZIP" >&2; exit 1; }
    echo "Mission Planner from $MP_ZIP"
    zip=$MP_ZIP
else
    command -v gh >/dev/null || { echo "Install the GitHub CLI first: brew install gh && gh auth login" >&2; exit 1; }
    run=${MP_RUN:-$(gh run list --repo "$repo" --workflow macos-native.yml --branch macos-arm64 \
        --status success --limit 1 --json databaseId --jq '.[0].databaseId')}
    [[ -n $run ]] || { echo "No successful native build found in $repo" >&2; exit 1; }
    echo "Mission Planner from https://github.com/$repo/actions/runs/$run"
    gh run download "$run" --repo "$repo" --name "$artifact" --dir "$work"
    zip="$work/$artifact.zip"
fi
ditto -x -k "$zip" "$work/app"
app="$work/app/Mission Planner.app"
[[ -d $app ]] || { echo "The download has no Mission Planner.app" >&2; exit 1; }
codesign --verify --deep --strict "$app"

if pgrep -f 'Mission Planner.app/Contents/MacOS/MissionPlanner' >/dev/null; then
    echo "Quit Mission Planner first." >&2
    exit 1
fi
mkdir -p -- "$applications"
rm -rf -- "$applications/Mission Planner.app"
ditto "$app" "$applications/Mission Planner.app"
# (nothing should carry it, as nothing went through a browser)
xattr -dr com.apple.quarantine "$applications/Mission Planner.app" 2>/dev/null || true
echo "Installed: $applications/Mission Planner.app"
[[ ${MP_NO_OPEN:-} == 1 ]] || open "$applications/Mission Planner.app"
