"""Fails when an OWASP ZAP report holds a High alert (PRD 8: "OWASP ZAP baseline scan ต้องไม่มี High").

ZAP's baseline script marks every rule WARN by default and has no switch for "fail on High only",
so the scan runs with -I (never fail on a warning) and the decision is made here, from the report.
Everything is printed either way, so a Medium that creeps in is seen before it becomes a High.
"""

import json
import sys

HIGH = "3"

report = json.load(open(sys.argv[1], encoding="utf-8"))
high = []

for site in report.get("site", []):
    for alert in site.get("alerts", []):
        line = f"{alert['riskdesc']:<28} {alert['name']} ({alert['count']})"
        print(line)
        if alert["riskcode"] == HIGH:
            high.append(line)

if high:
    print(f"\n{len(high)} High alert(s):")
    print("\n".join(high))
    sys.exit(1)

print("\nNo High alerts.")
