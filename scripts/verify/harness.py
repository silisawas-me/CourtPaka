"""Shared plumbing for the browser checks: where the stack is, who to sign in as, how to report."""

import base64
import datetime
import pathlib
import re
import subprocess
import sys
import urllib.parse
import uuid

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


def new_booker(page) -> str:
    """A booker with no venue of their own and no history, made through the API so the script can
    be run again without tripping the one-hold-at-a-time rule (PRD S-22)."""
    email = f"booker-{uuid.uuid4().hex[:12]}@example.com"
    # The server states the version it will accept, the same way the register page asks for it.
    version = page.request.get(f"{BASE}/api/auth/privacy-policy").json()["version"]
    registered = page.request.post(
        f"{BASE}/api/auth/register",
        data={
            "email": email,
            "password": PASSWORD,
            "privacyPolicyVersion": version,
            "language": "th",
            "phoneNumber": None,
        },
    )
    if registered.status != 201:
        raise RuntimeError(f"Could not register a booker: {registered.status} {registered.text()}")

    verify_email(page, email)
    return email


def verify_email(page, email: str) -> None:
    """Booking needs a verified address (PRD US-01). The link is emailed, and in Development the
    email sender logs it, so the check follows the same link a real booker would click."""
    logs = subprocess.run(
        ["docker", "compose", "logs", "--no-log-prefix", "api"],
        cwd=pathlib.Path(__file__).resolve().parents[2],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=True,
    ).stdout

    # The sender logs the recipient on one line and the body on the next, so the newest link under
    # this address is the one to follow. Registering twice would leave an older one above it.
    link = None
    addressed = False
    for line in logs.splitlines():
        if line.startswith("      Email to ") or "Email to " in line:
            addressed = f"Email to {email} " in line or f"Email to {email}[" in line
        elif addressed:
            found = re.search(r"(https?://\S*/verify-email\?\S+)", line)
            if found:
                link = found.group(1)
                addressed = False

    if link is None:
        raise RuntimeError(f"No verification link was logged for {email}.")

    query = urllib.parse.parse_qs(urllib.parse.urlparse(link.rstrip(".")).query)
    verified = page.request.post(
        f"{BASE}/api/auth/verify-email",
        data={"userId": query["userId"][0], "token": query["token"][0]},
    )
    if verified.status != 204:
        raise RuntimeError(f"Could not verify {email}: {verified.status} {verified.text()}")


def login(page, email: str, password: str = PASSWORD) -> None:
    page.fill("#email", email)
    page.fill("#password", password)
    page.click("button[type=submit]")


def sign_in(page, email: str) -> None:
    """The whole sequence: open the page, sign in, and wait to land.

    The language is set to Thai on the way in. An account keeps whichever one it was last left
    on, so a script that did not say would read whatever the run before it happened to choose —
    and the calendar, which prints its months in that language, is walked by reading them."""
    page.goto(f"{BASE}/login")
    login(page, email)
    page.wait_for_url(f"{BASE}/")
    page.click("[data-testid=language-th]")


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


THAI_MONTHS_FULL = [
    "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
    "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม",
]


def venue_today():
    """Today where the venues are. The server validates against the Bangkok date, so a check run
    from another zone must ask about the same day the server would."""
    return datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=7))).date()


def day_is_offered(page, date) -> bool:
    """Whether the open calendar will let that day be chosen. It walks to the month first: a day in
    a month the calendar is not showing has no cell at all, and reading that as "not offered" is how
    a check passes without ever reaching the boundary it is about."""
    reached = show_month(page, date)
    cell = page.locator(f'[aria-label="{calendar_label(date)}"]')
    return reached and cell.count() == 1 and cell.get_attribute("aria-disabled") != "true"


def calendar_label(date) -> str:
    """How the datepicker labels one day, which names the month as well as the number."""
    return f"{date.day} {THAI_MONTHS_FULL[date.month - 1]} {date.year + 543}"


def thai_date(date) -> str:
    return f"{date.day} {THAI_MONTHS[date.month - 1]} {date.year + 543}"


def pick_date(page, date) -> None:
    """Drives the calendar, because the field itself is read-only: Intl prints Thai dates but
    cannot read one back, so typing into it would be thrown away."""
    if page.locator("mat-calendar").count() == 0:
        page.click("mat-datepicker-toggle button")
        page.wait_for_selector("mat-calendar")

    show_month(page, date)
    page.click(f'[aria-label="{calendar_label(date)}"]')
    page.wait_for_selector("mat-calendar", state="detached")


def show_month(page, date) -> bool:
    """Walks the open calendar to the month holding that date, and says whether it got there. The
    calendar opens on whatever the field holds, which can be either side of the target, so it walks
    in the direction that closes the gap and stops when the picker's own bounds block the way."""
    period = page.locator("mat-calendar .mat-calendar-period-button")
    for _ in range(24):
        showing = period.inner_text().strip()
        if shown_month(page) == (date.year, date.month):
            return True

        forward = shown_month(page) < (date.year, date.month)
        step = page.locator(".mat-calendar-next-button" if forward else ".mat-calendar-previous-button")
        # The arrow is disabled at the picker's own bounds, which is a real answer: the month cannot
        # be reached because every day in it is outside the window.
        if not step.is_enabled():
            return False

        step.click()
        # The label is what says the move landed. Reading the month back before it does walks past
        # the target and into the arrow the picker has just disabled.
        page.wait_for_function(
            "([element, before]) => element.innerText.trim() !== before",
            arg=[period.element_handle(), showing],
        )
    return False


# The calendar prints its months in whatever language the account is set to, and the account
# keeps that choice between runs — so a script that could only read one of them would pass or
# fail by what the run before it did.
ENGLISH_MONTHS = [
    "JAN", "FEB", "MAR", "APR", "MAY", "JUN",
    "JUL", "AUG", "SEP", "OCT", "NOV", "DEC",
]


def shown_month(page) -> tuple[int, int]:
    """The month the calendar is showing, as (year, month) in the common era."""
    abbreviation, year = page.locator(
        "mat-calendar .mat-calendar-period-button"
    ).inner_text().strip().rsplit(" ", 1)

    if abbreviation in THAI_MONTHS:
        # Thai prints the Buddhist era, which is the common era plus 543.
        return int(year) - 543, THAI_MONTHS.index(abbreviation) + 1

    shortened = abbreviation.upper()[:3]
    return int(year), ENGLISH_MONTHS.index(shortened) + 1


def thai_month_year(date) -> str:
    return f"{THAI_MONTHS[date.month - 1]} {date.year + 543}"


def take_first_free_hour(page, venue_id, date, skip=0):
    """Picks an hour off the grid and confirms it, the way a booker does. Answers the booking the
    server made, and leaves the page wherever confirming led."""
    page.goto(f"{BASE}/book/{venue_id}?date={date.isoformat()}")
    page.wait_for_selector("[data-testid=availability-grid]")

    cell = page.locator("td.free").nth(skip).get_attribute("data-testid")
    page.click(f"[data-testid={cell}] button")
    page.wait_for_selector("[data-testid=booking-summary]")

    with page.expect_response(lambda response: response.url.endswith("/api/bookings")) as answer:
        page.click("[data-testid=book]")

    # Confirming lands on the page that pays for the hold, and every caller was waiting for it.
    page.wait_for_selector("[data-testid=countdown]")
    return answer.value


# The seeded venue is the only approved one, and only an approved venue can be edited, so every
# script drives that one rather than whichever link happens to come first.
SEEDED_VENUE = "Development Court"


def seeded_venue_id(page) -> str:
    """The id of the venue every script drives, asked for rather than assumed."""
    return page.request.get(f"{BASE}/api/venues/search?q={SEEDED_VENUE}").json()[0]["id"]


def as_upload(name: str, content: bytes, mime: str) -> dict:
    """Playwright takes a file from memory, so nothing is left behind in the temp directory."""
    return {"name": name, "mimeType": mime, "buffer": content}


def send_slip(page, upload):
    """Puts a file into the slip control and waits for the server's answer."""
    with page.expect_response(lambda response: response.url.endswith("/slip")) as answer:
        control(page, "send-slip").set_input_files(upload)
    return answer.value


def real_jpeg() -> bytes:
    """A one-pixel JPEG a browser can actually decode, with a tail belonging to this call alone.

    A header with text after it is not decodable, so a check that the slip is on screen would pass
    against a broken-image icon. The tail keeps two runs from sharing bytes: the duplicate-slip
    flag is per venue and forever (PRD BR-07), so a re-run would otherwise flag a slip the check
    did not mean to flag."""
    encoded = pathlib.Path(__file__).with_name("slip.jpg.b64").read_text()
    return base64.b64decode(encoded) + uuid.uuid4().bytes


def open_seeded_venue(page) -> str:
    """Signs the current session into the venue list and answers its href."""
    page.goto(f"{BASE}/venues")
    page.wait_for_selector("[data-testid=venue-list] a")
    return (
        page.locator("[data-testid=venue-list] a", has_text=SEEDED_VENUE)
        .first.get_attribute("href")
    )
