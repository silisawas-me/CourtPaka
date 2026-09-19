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


# Material renders a checkbox as an <input> inside its host and a slide toggle as a
# <button role="switch">, so a check written against the host would touch neither. Playwright
# reads both through the role, which is the part Material does not get to change.
def control(page, test_id: str):
    return page.locator(f'[data-testid="{test_id}"]').locator("input, button").first


# The Buddhist era and the month names the pages print, so a check can say which date it expects
# rather than only which year.
THAI_MONTHS = [
    "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.",
    "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค.",
]


def thai_date(date) -> str:
    return f"{date.day} {THAI_MONTHS[date.month - 1]} {date.year + 543}"


def pick_date(page, date) -> None:
    """Drives the calendar, because the field itself is read-only: Intl prints Thai dates but
    cannot read one back, so typing into it would be thrown away."""
    page.click("mat-datepicker-toggle button")
    page.wait_for_selector("mat-calendar")

    # The calendar opens on the current month; step forward until the header names the target.
    for _ in range(24):
        if page.locator("mat-calendar .mat-calendar-period-button").inner_text().strip() == thai_month_year(date):
            break
        page.click(".mat-calendar-next-button")
    page.click(f'.mat-calendar-body-cell-content:text-is("{date.day}")')
    page.wait_for_selector("mat-calendar", state="detached")


def thai_month_year(date) -> str:
    return f"{THAI_MONTHS[date.month - 1]} {date.year + 543}"
