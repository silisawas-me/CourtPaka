"""Shared plumbing for the browser checks: where the stack is, who to sign in as, how to report."""

import pathlib
import sys

BASE = "http://localhost:8080"
OWNER = "owner@courtpaka.local"
STAFF = "staff@courtpaka.local"
PASSWORD = "DevPassword1"


class Checks:
    """Records what passed, writes a screenshot per check, and decides the exit code."""

    def __init__(self, script: str) -> None:
        self.shots = pathlib.Path(script).parent / "shots" / pathlib.Path(script).stem
        self.shots.mkdir(parents=True, exist_ok=True)
        self.passed: list[str] = []
        self.failed: list[str] = []

    def __call__(self, name: str, condition: bool, page=None) -> None:
        (self.passed if condition else self.failed).append(name)
        print(("PASS  " if condition else "FAIL  ") + name)
        if page is not None:
            safe = name.replace(" ", "_").replace("/", "-")
            page.screenshot(path=str(self.shots / f"{safe}.png"), full_page=True)

    def summarise(self) -> None:
        print(f"\n{len(self.passed)} passed, {len(self.failed)} failed")
        for name in self.failed:
            print("  FAILED: " + name)
        sys.exit(1 if self.failed else 0)


def login(page, email: str, password: str = PASSWORD) -> None:
    page.fill("#email", email)
    page.fill("#password", password)
    page.click("button[type=submit]")
