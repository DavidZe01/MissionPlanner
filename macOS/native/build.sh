#!/usr/bin/env bash
# Build Mission Planner as a native macOS app (.NET for macOS, Cocoa Windows Forms).
# On Linux this only compiles (MissionPlannerMacCompileOnly); on a Mac it makes the .app.
# Usage: macOS/native/build.sh [extra dotnet build arguments]
set -euo pipefail
native_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source_dir=$(cd -- "$native_dir/../.." && pwd)
extra=()
# Linux can compile the code but not bundle a macOS app
[[ $(uname -s) == Darwin ]] || extra+=(-p:MissionPlannerMacCompileOnly=true -p:OutputType=Library -p:IsMacEnabled=false -p:BundleOriginalResources=false -p:AppManifestDetectionEnabled=false -p:StartupObject=)
cd -- "$source_dir"
dotnet build MissionPlanner.csproj -c Release \
    "-p:DirectoryBuildTargetsPath=$native_dir/Build.targets" -p:MissionPlannerMacNative=true \
    -p:MissionPlannerLinuxBuild=true \
    "${extra[@]}" "$@"
