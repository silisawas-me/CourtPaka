"""What a booker on a phone waits for before the court grid is there (PRD 8: LCP <= 2.5 s).

Runs Lighthouse against the local stack the same way every time — mobile, simulated 4G — and
prints the numbers that decide whether the target is met. The figure that counts is the one from
the PRD machine after warm-up; this is how a change is compared with the one before it.

    docker compose --profile full up -d --build
    python scripts/perf/grid_lcp.py            # the seeded venue, tomorrow
    python scripts/perf/grid_lcp.py --runs 3   # median of three, for a smaller spread
"""

import argparse
import json
import pathlib
import statistics
import subprocess
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "verify"))

from harness import BASE, seeded_venue_id, venue_today  # noqa: E402
from playwright.sync_api import sync_playwright  # noqa: E402

MEASURED = {
    "largest-contentful-paint": "LCP",
    "first-contentful-paint": "FCP",
    "total-blocking-time": "TBT",
    "speed-index": "Speed Index",
}


def lighthouse(url: str) -> dict:
    """One Lighthouse run, as a phone on 4G. Returns the audits by id."""
    with tempfile.TemporaryDirectory() as folder:
        report = pathlib.Path(folder) / "report.json"
        finished = subprocess.run(
            [
                "npx",
                "--yes",
                "lighthouse",
                url,
                "--only-categories=performance",
                "--form-factor=mobile",
                "--screenEmulation.mobile",
                "--throttling-method=simulate",
                "--output=json",
                f"--output-path={report}",
                '--chrome-flags="--headless=new --no-sandbox"',
                "--quiet",
            ],
            capture_output=True,
            text=True,
            shell=sys.platform == "win32",
        )
        if not report.exists():
            print(finished.stdout[-2000:], finished.stderr[-2000:], sep="\n")
            raise SystemExit("Lighthouse wrote no report")

        return json.loads(report.read_text(encoding="utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--url", default=None, help="Defaults to the seeded venue's grid.")
    asked = parser.parse_args()

    url = asked.url
    courts = None
    if url is None:
        with sync_playwright() as p:
            browser = p.chromium.launch()
            page = browser.new_page()
            venue = seeded_venue_id(page)
            date = venue_today().isoformat()
            # How big the grid is decides how long it takes to draw, and the seeded venue gains a
            # court every time a check adds one. Two runs compare only at the same size.
            grid = page.request.get(f"{BASE}/api/venues/{venue}/availability?date={date}").json()
            courts = len(grid["courts"])
            browser.close()
        url = f"{BASE}/book/{venue}?date={date}"

    print(f"Measuring {url}" + (f" ({courts} courts)" if courts else "") + "\n")
    scores: list[float] = []
    values: dict[str, list[float]] = {audit: [] for audit in MEASURED}

    for run in range(1, asked.runs + 1):
        result = lighthouse(url)
        scores.append(result["categories"]["performance"]["score"])
        for audit in MEASURED:
            values[audit].append(result["audits"][audit]["numericValue"])
        print(
            f"run {run}: score {scores[-1]:.2f} · "
            + " · ".join(
                f"{name} {values[audit][-1] / 1000:.2f}s" for audit, name in MEASURED.items()
            )
        )

    lcp = statistics.median(values["largest-contentful-paint"]) / 1000
    print(f"\nmedian score {statistics.median(scores):.2f}")
    for audit, name in MEASURED.items():
        print(f"median {name}: {statistics.median(values[audit]) / 1000:.2f}s")

    # The target is PRD 8's; the run that decides it is on the PRD machine, not this one.
    print(f"\nPRD 8 wants LCP <= 2.50s on mobile 4G — this stack: {lcp:.2f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
