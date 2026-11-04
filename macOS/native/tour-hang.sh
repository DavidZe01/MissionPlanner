#!/bin/sh
# Run by the screen tour (macOS/ScreenTour.cs, TOUR_HANG_CMD) when a step does not finish:
# records where every thread of Mission Planner (TOUR_PID) is, for the step TOUR_ITEM.
#   hang-<item>-native.txt   native stacks (macOS sample), first: it always answers
#   hang-<item>-managed.txt  .NET stacks (dotnet-stack, through the runtime's diagnostics IPC),
#                            which needs the runtime to respond: at most 40 s
out="${TOUR_OUT:-${TMPDIR:-/tmp}}/hang-${TOUR_ITEM:-unknown}"
if command -v sample > /dev/null 2>&1; then
    sample "$TOUR_PID" 2 -file "$out-native.txt" > /dev/null 2>&1
fi
stack="$HOME/.dotnet/tools/dotnet-stack"
if [ -x "$stack" ]; then
    DOTNET_ROLL_FORWARD=Major perl -e 'alarm 40; exec @ARGV' "$stack" report -p "$TOUR_PID" > "$out-managed.txt" 2>&1
fi
exit 0
