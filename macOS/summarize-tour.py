#!/usr/bin/env python3
"""Summarise screen-tour reports (tour-report.tsv) by category.

Usage: summarize-tour.py <tour-report.tsv> [more reports...]
Items are 'screen:*', 'page:*', 'feature:*' and 'connect:*'. A page or feature
that passes in any report counts as working (the connected tour exercises
pages that need a vehicle)."""
import collections
import sys

results = collections.OrderedDict()
for path in sys.argv[1:]:
    with open(path, encoding="utf-8", errors="replace") as f:
        next(f, None)
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 2:
                continue
            item, result = parts[0], parts[1]
            error = parts[3] if len(parts) > 3 else ""
            ok = result == "OK"
            prev = results.get(item)
            if prev is None or (ok and not prev[0]):
                results[item] = (ok, error)

by_cat = collections.OrderedDict()
for item, (ok, error) in results.items():
    cat = item.split(":", 1)[0]
    by_cat.setdefault(cat, []).append((item, ok, error))

total = passed = 0
for cat, items in by_cat.items():
    n = len(items)
    p = sum(1 for _, ok, _ in items if ok)
    total += n
    passed += p
    print(f"{cat:10s} {p:4d}/{n:<4d}")
    for item, ok, error in items:
        if not ok:
            print(f"   FAIL {item}: {error[:160]}")
print(f"{'total':10s} {passed:4d}/{total:<4d} ({100.0 * passed / max(total, 1):.1f}%)")
