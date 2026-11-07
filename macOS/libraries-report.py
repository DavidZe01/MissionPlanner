#!/usr/bin/env python3
"""Libraries the Mission Planner process loaded, from DYLD_PRINT_LIBRARIES output in a log.

dyld prints "dyld[<pid>]: <uuid> <path>" for each image, the process's executable first.
Only processes whose executable is Mission Planner's count: child processes it starts (the
Log Analyzer's python3, say) load their own libraries, which are not the app's.

Usage: libraries-report.py <log> <libraries-loaded.txt>
Prints a summary and the Homebrew (/opt/homebrew, /usr/local) libraries the app loaded;
writes the app's sorted library paths to the second file.
"""
import re
import sys

line_re = re.compile(r"dyld\[(\d+)\]: <[^>]*> (\S.*?)\s*$")

images = {}  # pid -> list of paths, in load order
with open(sys.argv[1], errors="replace") as f:
    for line in f:
        m = line_re.search(line)
        if m:
            images.setdefault(m.group(1), []).append(m.group(2))

app_pids = [pid for pid, paths in images.items() if paths[0].endswith("Contents/MacOS/MissionPlanner")]
others = sorted({paths[0] for pid, paths in images.items() if pid not in app_pids})
app_libs = sorted({p for pid in app_pids for p in images[pid]})
homebrew = [p for p in app_libs if p.startswith(("/opt/homebrew/", "/usr/local/"))]

with open(sys.argv[2], "w") as out:
    out.write("".join(p + "\n" for p in app_libs))

print("Mission Planner processes: %d, libraries loaded: %d, from the app: %d, bundled GStreamer: %d"
      % (len(app_pids), len(app_libs), sum("Mission Planner.app/" in p for p in app_libs),
         sum("MonoBundle/gstreamer/" in p for p in app_libs)))
print("other processes (not counted): %s" % (", ".join(others) or "none"))
print("LIBRARIES FROM HOMEBREW OR /usr/local: %d" % len(homebrew))
for p in homebrew[:20]:
    print("  " + p)
