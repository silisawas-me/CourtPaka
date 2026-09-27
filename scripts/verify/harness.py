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


def logged_mail(email: str) -> list[str]:
    """Every message the Development sender logged to this address, oldest first, each as its
    header line and body. Entries in the log start at column 0 ("info: ..."), and everything a
    message says is indented under it, so a message runs until the next unindented line."""
    logs = subprocess.run(
        ["docker", "compose", "logs", "--no-log-prefix", "api"],
        cwd=pathlib.Path(__file__).resolve().parents[2],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=True,
    ).stdout

    messages: list[str] = []
    current: list[str] | None = None
    for line in logs.splitlines():
        if current is not None and line and not line[0].isspace():
            messages.append("\n".join(current))
            current = None
        if f"Email to {email} [" in line:
            current = [line.strip()]
        elif current is not None:
            current.append(line.strip())
    if current is not None:
        messages.append("\n".join(current))
    return messages


def run_out_hold(booking_id: str) -> None:
    """Makes a hold's fifteen minutes be up, in the database.

    There is no endpoint for this and there should not be: "expire that booking" is not something
    a real caller may ask for, and adding it would be adding a way to release somebody else's
    hours. So the check reaches past the API, the same way verify_email reaches into the logs for
    a link the API will not hand over."""
    done = subprocess.run(
        [
            "docker", "compose", "exec", "-T", "db",
            "psql", "-U", "courtbooking", "-d", "courtbooking", "-c",
            f"""UPDATE "Bookings" SET "HoldExpiresAt" = now() - interval '1 minute'
                WHERE "Id" = '{booking_id}' AND "Status" = 1;""",
        ],
        cwd=pathlib.Path(__file__).resolve().parents[2],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=True,
    )
    if "UPDATE 1" not in done.stdout:
        raise RuntimeError(f"Could not run out the hold {booking_id}: {done.stdout}{done.stderr}")


def booking_status(booking_id: str) -> int:
    """Reads a booking's status straight from the database.

    Asking the API would answer the question and change it: every read of a booker's own list
    ends that booker's holds whose time is up, which is exactly the behaviour a check about the
    caretaker must not lean on."""
    done = subprocess.run(
        [
            "docker", "compose", "exec", "-T", "db",
            "psql", "-U", "courtbooking", "-d", "courtbooking", "-t", "-A", "-c",
            f"""SELECT "Status" FROM "Bookings" WHERE "Id" = '{booking_id}';""",
        ],
        cwd=pathlib.Path(__file__).resolve().parents[2],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=True,
    )
    return int(done.stdout.strip())


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


def pick_date(page, date, toggle="mat-datepicker-toggle button") -> None:
    """Drives the calendar, because the field itself is read-only: Intl prints Thai dates but
    cannot read one back, so typing into it would be thrown away. `toggle` names which calendar,
    for a page carrying more than one."""
    # The grid page does not download the calendar until somebody asks for it (PRD 8's LCP
    # target). What stands there until then is a placeholder, and pressing it both fetches the
    # calendar and opens it — the same one press a booker makes.
    if page.locator(toggle).count() == 0 and page.locator("[data-testid=day-placeholder]").count():
        page.click("[data-testid=day-placeholder]")
        page.wait_for_selector("mat-calendar")

    if page.locator("mat-calendar").count() == 0:
        page.click(toggle)
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


DAYS = ("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")


# What the seed gives the staff account: everything except reading reports and changing
# settings (VenuePermissions.StaffDefault).
STAFF_DEFAULT = ["VerifySlip", "ManageBookings", "CloseCourt"]


def staff_can(browser, venue_id, *permissions) -> None:
    """Sets what the seeded staff account may do at the seeded venue.

    `venue_ui.py` hands the staff more permissions because that is what it tests, and leaves them
    wherever its last check left them — so a script that checks what somebody *without* a
    permission sees would pass alone and fail after it. Ask for the standing you need; do not
    assume the script before you left it (rule 7 in this directory's README)."""
    page = browser.new_page()
    sign_in(page, OWNER)
    members = page.request.get(f"{BASE}/api/venues/{venue_id}/members").json()
    staff = next(one for one in members if one["email"] == STAFF)

    set_to = page.request.put(
        f"{BASE}/api/venues/{venue_id}/members/{staff['userId']}/permissions",
        data={"permissions": list(permissions) or STAFF_DEFAULT},
    )
    if set_to.status not in (200, 204):
        raise RuntimeError(f"Could not set the staff's permissions: {set_to.status}")
    page.close()


def ensure_bookable(browser, venue_id) -> None:
    """Puts the seeded venue back to the hours every booking script assumes: open 06:00 to 22:00,
    every day, from today.

    venue_settings.py edits those hours because they are what it is about, and leaves the venue
    wherever its last check left it — so a script that ran afterwards would find the venue closed
    and no free hour anywhere. Setting up repeatable state is the first rule in this directory's
    README, and this is the state the rest of them mean."""
    page = browser.new_page()
    sign_in(page, OWNER)
    published = page.request.put(
        f"{BASE}/api/venues/{venue_id}/opening-hours",
        data={
            "effectiveFrom": venue_today().isoformat(),
            "days": [{"day": day, "opensHour": 6, "closesHour": 22} for day in DAYS],
        },
    )
    if published.status != 200:
        raise RuntimeError(f"Could not open the venue for business: {published.status}")

    # And at the prices the seed sets, which is what the scripts that read a price expect.
    # venue_pricing.py rewrites them because that is what it is about, and leaves them wherever
    # its last check left them (PRD: 200 an hour, 300 from 18:00).
    priced = page.request.put(
        f"{BASE}/api/venues/{venue_id}/prices",
        data={
            "bands": [
                band
                for day in DAYS
                for band in (
                    {"day": day, "fromHour": 6, "toHour": 18, "bahtPerHour": 200},
                    {"day": day, "fromHour": 18, "toHour": 22, "bahtPerHour": 300},
                )
            ],
        },
    )
    if priced.status != 200:
        raise RuntimeError(f"Could not price the venue: {priced.status} {priced.text()}")

    # And with no court shut. court_closures.py closes courts because that is what it is about,
    # and a court left shut is an hour with no price — which reads to every other script as a
    # grid that has gone wrong rather than as a court somebody closed.
    for shut in page.request.get(f"{BASE}/api/venues/{venue_id}/closures").json():
        if shut["liftedAt"] is None:
            page.request.post(f"{BASE}/api/venues/{venue_id}/closures/{shut['id']}/lift")

    # And asking for the whole price up front, which is what every script that reads an amount
    # assumes. deposit.py lowers it because that is what it is about (PRD US-28), and a venue left
    # asking for a quarter turns every "the code is for the price" check into a puzzle.
    whole = page.request.put(
        f"{BASE}/api/venues/{venue_id}/deposit", data={"percent": 100}
    )
    if whole.status != 204:
        raise RuntimeError(f"Could not reset the deposit: {whole.status} {whole.text()}")

    # And counting misses the way the product says, with no hour treated as peak. deposit_risk.py
    # moves these because that is what it is about (PRD US-28).
    counting = page.request.put(
        f"{BASE}/api/venues/{venue_id}/risk-rule",
        data={
            "on": True,
            "lookbackDays": 60,
            "halfAt": 2,
            "fullAt": 3,
            "peakFromHour": None,
            "peakUntilHour": None,
        },
    )
    if counting.status != 204:
        raise RuntimeError(f"Could not reset the risk rule: {counting.status} {counting.text()}")

    # And with no group coming every week. series.py writes one down because that is what it is
    # about (PRD US-30), and one left standing keeps booking an hour of every week from now on —
    # which reads to every other script as a floor that has gone wrong rather than as a group
    # somebody agreed.
    stop_every_series(page, venue_id)

    # And with nothing on the package board. packages.py puts offers on it because that is what
    # it is about (PRD US-31), and one left there is a row every other script's screenshots and
    # counts have to step around.
    clear_package_board(page, venue_id)

    page.close()


def clear_package_board(page, venue_id) -> None:
    """Takes every offer off the venue's board. Through the API rather than the screen: it is the
    setup, not the subject."""
    for offer in page.request.get(f"{BASE}/api/venues/{venue_id}/packages/types").json():
        if offer["withdrawnAt"] is None:
            page.request.post(
                f"{BASE}/api/venues/{venue_id}/packages/types/{offer['typeId']}/withdraw")


def stop_every_series(page, venue_id) -> None:
    """Leaves the venue with no standing arrangement running, and the weeks they had booked given
    back. Through the API rather than the screen: it is the setup, not the subject."""
    for one in page.request.get(f"{BASE}/api/venues/{venue_id}/series").json():
        if one["state"] == "Running":
            page.request.post(
                f"{BASE}/api/venues/{venue_id}/series/{one['seriesId']}/stop",
                data={"note": None},
            )


def clear_waiting(page, venue_id, date) -> None:
    """Answers everything an earlier run left waiting at this venue, so a run starts from a venue
    with nothing on its mind. Through the API rather than the screen: it is the setup, not the
    subject."""
    for slip in page.request.get(f"{BASE}/api/venues/{venue_id}/slip-queue").json():
        page.request.post(
            f"{BASE}/api/venues/{venue_id}/slip-queue/{slip['bookingId']}/confirm")

    for booking in page.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={date.isoformat()}"
    ).json():
        if booking["can"]["settlePayment"]:
            page.request.post(
                f"{BASE}/api/venues/{venue_id}/bookings/{booking['bookingId']}/settle-payment",
                data={"paymentReceived": False})


def open_seeded_venue(page) -> str:
    """Signs the current session into the venue list and answers its href."""
    page.goto(f"{BASE}/venues")
    page.wait_for_selector("[data-testid=venue-list] a")
    return (
        page.locator("[data-testid=venue-list] a", has_text=SEEDED_VENUE)
        .first.get_attribute("href")
    )
