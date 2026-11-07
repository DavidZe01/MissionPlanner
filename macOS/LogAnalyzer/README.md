# Log Analyzer (macOS)

ArduPilot's Log Analyzer, `Tools/LogAnalyzer` at tag Copter-4.5.7
(2a3dc4b7bf2507120f7378a7b2fde73185e0c325), the last release that had it, GPLv3 like
Mission Planner. On Windows Mission Planner downloads it frozen as `runner.exe`;
`macOS/native/build.sh` puts this copy in the app (`Contents/MonoBundle/LogAnalyzer`) and
`Utilities/LogAnalyzer.cs` runs it with `python3`.

Changes, each marked "Mission Planner for macOS" in the source, so it runs on the Python 3
macOS has (no numpy):
- `DataflashLog.py`, `tests/TestVibration.py`: `statistics` instead of `numpy` (mean,
  population standard deviation); `numpy` of a dict's values also failed on Python 3;
- `LogAnalyzer.py`: a test module that cannot be imported (`TestOptFlow` needs numpy and
  matplotlib) is skipped, and a test that raises (`TestCompass` compares a float with None,
  which Python 3 refuses) is reported as UNKNOWN, instead of either ending the whole run.

`macOS/tests/underpowered.log.gz` is the analyzer's own `examples/underpowered.log`, which
the screen tour runs through it (`feature:log-analyzer`).
