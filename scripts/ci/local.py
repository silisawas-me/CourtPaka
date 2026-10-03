"""Everything CI would check, on this machine, as one answer.

CI has not been able to run since the account stopped being given runners, and in the meantime
"verified" has meant whatever was remembered to be run that day. This is the same list of checks
the workflow makes, in the same order, so that the word means one thing.

It is not a replacement. A green run here says the code is sound; it does not say it is sound on
a clean machine, which is half of what a continuous integration server is for. It says so at the
end rather than letting anyone forget.

    python scripts/ci/local.py            everything
    python scripts/ci/local.py --quick    everything but the flows and the scan
"""

import argparse
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# The flows the workflow itself runs (ci.yml, job `flows`). The rest exist, but these six are the
# gate. Since the booker's pages were taken out (docs/plan/cut-booker.md) the six are the venue's.
FLOWS = ["doors", "venue_shell", "close_drawer", "counter_money", "walk_in", "now_board", "timeline", "booking_list", "venue_setup"]


class Check:
    """One line of the report: what it is, how to do it, and whether it needs the stack up."""

    def __init__(self, job, name, command, cwd=ROOT, needs_stack=False, slow=False):
        self.job = job
        self.name = name
        self.command = command
        self.cwd = cwd
        self.needs_stack = needs_stack
        self.slow = slow


def checks():
    web = ROOT / "frontend"
    return [
        Check("backend", "build", "dotnet build backend -c Release -warnaserror", slow=True),
        Check("backend", "tests", "dotnet test backend --no-build -c Release", slow=True),
        Check("frontend", "tests", "npm test -- --watch=false", cwd=web),
        Check("frontend", "build", "npm run build", cwd=web),
        Check("frontend", "format", "npm run format:check", cwd=web),
        Check("frontend", "audit", "npm audit --omit=dev --audit-level=high", cwd=web),
        Check("docker", "stack builds and comes up",
              "docker compose --profile full up -d --build --wait", slow=True),
        *[Check("flows", flow, f"python scripts/verify/{flow}.py", needs_stack=True)
          for flow in FLOWS],
        Check("zap", "no High alerts", "zap", needs_stack=True, slow=True),
    ]


def zap():
    """The baseline scan, aimed the way this machine can reach its own stack.

    The workflow uses `--network host`, which on Docker Desktop is not the host's network at all,
    so the scan would find nothing and pass for the wrong reason.
    """
    reports = ROOT / "zap"
    reports.mkdir(exist_ok=True)
    scan = subprocess.run(
        ["docker", "run", "--rm", "-v", f"{reports}:/zap/wrk:rw",
         "ghcr.io/zaproxy/zaproxy:stable", "zap-baseline.py",
         "-t", "http://host.docker.internal:8080", "-J", "report.json", "-I"],
        cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")

    report = reports / "report.json"
    if not report.exists():
        return False, (scan.stdout or scan.stderr or "").strip()[-600:]

    gate = subprocess.run([sys.executable, "scripts/ci/zap_high.py", str(report)],
                          cwd=ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    shutil.rmtree(reports, ignore_errors=True)
    return gate.returncode == 0, gate.stdout.strip().splitlines()[-1] if gate.stdout else ""


def run(check):
    if check.command == "zap":
        return zap()
    done = subprocess.run(check.command, cwd=check.cwd, shell=True,
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    if done.returncode == 0:
        return True, ""
    tail = (done.stdout + done.stderr).strip().splitlines()
    return False, "\n      ".join(tail[-12:])


def main():
    # A Windows console is cp1252 until told otherwise, and half of what this prints is Thai.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")

    argue = argparse.ArgumentParser(description=__doc__)
    argue.add_argument("--quick", action="store_true",
                       help="skip what needs the stack standing: the flows and the scan")
    said = argue.parse_args()

    wanted = [c for c in checks() if not (said.quick and c.needs_stack)]
    started = time.monotonic()
    failed = []
    job = None

    for check in wanted:
        if check.job != job:
            job = check.job
            print(f"\n== {job}")
        began = time.monotonic()
        ok, why = run(check)
        took = time.monotonic() - began
        print(f"\r   {'PASS' if ok else 'FAIL'}  {check.name}  ({took:.0f}s)")
        if not ok:
            failed.append(check)
            if why:
                print(f"      {why}")

    print(f"\n{len(wanted) - len(failed)}/{len(wanted)} ผ่าน · {time.monotonic() - started:.0f}s")
    if failed:
        print("ตก: " + ", ".join(f"{c.job}/{c.name}" for c in failed))
        return 1

    print("\nนี่คือเครื่องนี้ ไม่ใช่เครื่องสะอาด — มันบอกว่าโค้ดใช้ได้ ไม่ได้บอกว่าตั้งต้นใหม่แล้วใช้ได้")
    return 0


if __name__ == "__main__":
    sys.exit(main())
