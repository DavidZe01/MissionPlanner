#!/usr/bin/env bash
# Compile the screen tour into a built application directory (the output of
# Linux/build.sh) and refresh its archive. Needs Mono's mcs (mono-devel).
set -euo pipefail
mac_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
app_dir=$(cd -- "${1:?usage: macOS/build-tests.sh <app-dir>}" && pwd)
facades=${MONO_FACADES_PATH:-/usr/lib/mono/4.5/Facades}
mcs -out:"$app_dir/ScreenTour.exe" \
    -r:System.Windows.Forms -r:System.Drawing \
    -r:"$app_dir/MissionPlanner.exe" -r:"$app_dir/Interfaces.dll" \
    -r:"$app_dir/MissionPlanner.Controls.dll" -r:"$app_dir/MissionPlanner.Utilities.dll" \
    -r:"$app_dir/MissionPlanner.Comms.dll" -r:"$app_dir/MissionPlanner.ArduPilot.dll" \
    -r:"$app_dir/MAVLink.dll" -r:"$app_dir/System.Memory.dll" -r:"$app_dir/OpenTK.GLControl.dll" -r:"$app_dir/OpenTK.dll" -r:"$facades/netstandard.dll" "$mac_dir/ScreenTour.cs"
cp -- "$app_dir/MissionPlanner.exe.config" "$app_dir/ScreenTour.exe.config"
mcs -out:"$app_dir/Probe.exe" \
    -r:"$app_dir/MissionPlanner.exe" -r:"$app_dir/Interfaces.dll" \
    -r:"$app_dir/MissionPlanner.Utilities.dll" -r:"$app_dir/MissionPlanner.Comms.dll" \
    -r:"$app_dir/MissionPlanner.ArduPilot.dll" -r:"$app_dir/MAVLink.dll" \
    -r:"$app_dir/System.Memory.dll" -r:"$facades/netstandard.dll" "$mac_dir/Probe.cs"
cp -- "$app_dir/MissionPlanner.exe.config" "$app_dir/Probe.exe.config"
parent=$(dirname -- "$app_dir"); name=$(basename -- "$app_dir")
if [[ -f $parent/$name.tar.gz ]]; then
    tar -C "$parent" -czf "$parent/$name.tar.gz" "$name"
    (cd -- "$parent" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
fi
echo "Built $app_dir/ScreenTour.exe"
