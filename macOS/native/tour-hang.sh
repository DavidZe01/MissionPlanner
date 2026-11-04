#!/bin/sh
# Run by the screen tour (macOS/ScreenTour.cs, TOUR_HANG_CMD) when a step does not finish:
# records where every thread of Mission Planner (TOUR_PID) is, for the step TOUR_ITEM.
#   hang-<item>-managed.txt  .NET stacks (dotnet-stack, through the runtime's diagnostics IPC)
#   hang-<item>-native.txt   native stacks (macOS sample)
out="${TOUR_OUT:-${TMPDIR:-/tmp}}/hang-${TOUR_ITEM:-unknown}"
stack="$HOME/.dotnet/tools/dotnet-stack"
if [ -x "$stack" ]; then
    DOTNET_ROLL_FORWARD=Major "$stack" report -p "$TOUR_PID" > "$out-managed.txt" 2>&1
fi
if command -v sample > /dev/null 2>&1; then
    sample "$TOUR_PID" 2 -file "$out-native.txt" > /dev/null 2>&1
fi
exit 0
