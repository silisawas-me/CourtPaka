"""Taking court-hours from the grid: picking, the summary, and the hold (US-03)."""

import datetime

import uuid

from harness import BASE, PASSWORD, SEEDED_VENUE, Checks, new_booker, sign_in, venue_today
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)


def sign_in_as_booker(page):
    """Booking needs an account, not venue membership — and a booker with no history, so the
    checks can be run again without the previous run's hold in the way."""
    sign_in(page, new_booker(page))


def picked_count(page, expected):
    """Waits for the summary to hold that many rows, rather than counting mid-render."""
    rows = page.locator("[data-testid=summary-slot]")
    try:
        expect(rows).to_have_count(expected, timeout=5_000)
    except AssertionError:
        return False
    return True


def open_grid(page, venue_id, date):
    page.goto(f"{BASE}/book/{venue_id}?date={date.isoformat()}")
    page.wait_for_selector("[data-testid=availability-grid]")


with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 390, "height": 844})

    venue_id = page.request.get(f"{BASE}/api/venues/search?q={SEEDED_VENUE}").json()[0]["id"]

    # 1. A visitor with no session can pick, but is sent to sign in rather than offered a booking.
    open_grid(page, venue_id, tomorrow)
    first = page.locator("td.free").first.get_attribute("data-testid")
    page.click(f"[data-testid={first}] button")
    page.wait_for_selector("[data-testid=booking-summary]")
    check("a signed-out visitor can pick an hour", picked_count(page, 1), page)
    check(
        "and is asked to sign in rather than offered a booking",
        page.locator("[data-testid=sign-in-to-book]").count() == 1
        and page.locator("[data-testid=book]").count() == 0,
    )

    # 2. A booker picks two hours and sees them added up before confirming.
    sign_in_as_booker(page)
    open_grid(page, venue_id, tomorrow)

    cells = [
        page.locator("td.free").nth(index).get_attribute("data-testid") for index in (0, 1)
    ]
    prices = []
    for test_id in cells:
        prices.append(int(page.locator(f"[data-testid={test_id}]").inner_text().strip()))
        page.click(f"[data-testid={test_id}] button")

    page.wait_for_selector("[data-testid=booking-summary]")
    check("two hours can be picked at once", picked_count(page, 2), page)
    check(
        "the summary adds them up before anything is confirmed",
        str(sum(prices)) in page.locator("[data-testid=summary-total]").inner_text(),
    )
    check(
        "a picked hour is marked on the grid",
        page.locator(f"[data-testid={cells[0]}]").get_attribute("class").find("picked") >= 0,
    )

    # Touching one again takes it back out.
    page.click(f"[data-testid={cells[1]}] button")
    check("touching a picked hour again drops it", picked_count(page, 1))
    page.click(f"[data-testid={cells[1]}] button")

    # 3. Confirming holds the hours.
    with page.expect_response(lambda response: response.url.endswith("/api/bookings")) as answer:
        page.click("[data-testid=book]")
    check("confirming is accepted", answer.value.status == 201)

    page.wait_for_selector("[data-testid=held-booking]")
    held = answer.value.json()
    # The API answers baht as a decimal, so 400 arrives as 400.0 and has to be read as a number.
    check(
        "the hold says what it costs",
        f"{held['totalBaht']:g}" in page.locator("[data-testid=held-total]").inner_text(),
        page,
    )
    check("the hold covers both hours", len(held["slots"]) == 2)
    check(
        "the hold lasts fifteen minutes",
        (
            datetime.datetime.fromisoformat(held["holdExpiresAt"])
            - datetime.datetime.fromisoformat(held["createdAt"])
        )
        == datetime.timedelta(minutes=15),
    )
    check("and the picks are spent", page.locator("[data-testid=booking-summary]").count() == 0)

    # 4. The hours are now taken, and the page says so without being asked again.
    page.wait_for_selector(f"[data-testid={cells[0]}].booked")
    check(
        "the hours it holds read as booked without a reload",
        page.locator(f"[data-testid={cells[0]}]").get_attribute("class").find("booked") >= 0,
        page,
    )
    check(
        "and cannot be picked",
        page.locator(f"[data-testid={cells[0]}] button").count() == 0,
    )

    # 5. One hold at a time.
    third = page.locator("td.free").first.get_attribute("data-testid")
    page.click(f"[data-testid={third}] button")
    with page.expect_response(lambda response: response.url.endswith("/api/bookings")) as second:
        page.click("[data-testid=book]")
    check("a second hold is refused while one is waiting", second.value.status == 409)
    check(
        "and the page says why in the reader's language",
        "รอชำระเงิน" in page.locator("[data-testid=booking-error]").inner_text(),
        page,
    )

    # 7. Another booker cannot take an hour that is already held.
    taken_court, taken_hour = cells[0].removeprefix("cell-").rsplit("-", 1)

    # 6. An address nobody has proved they can read cannot hold a court (PRD US-01).
    unverified = f"unverified-{uuid.uuid4().hex[:12]}@example.com"
    page.request.post(
        f"{BASE}/api/auth/register",
        data={
            "email": unverified,
            "password": PASSWORD,
            "privacyPolicyVersion": page.request.get(
                f"{BASE}/api/auth/privacy-policy").json()["version"],
            "language": "th",
            "phoneNumber": None,
        },
    )
    stranger = browser.new_page()
    sign_in(stranger, unverified)
    refused_unverified = stranger.request.post(
        f"{BASE}/api/bookings",
        data={
            "venueId": venue_id,
            "slots": [
                {"courtId": taken_court, "date": tomorrow.isoformat(), "hour": int(taken_hour) + 1}
            ],
        },
    )
    check("an unverified address cannot book", refused_unverified.status == 403)
    check(
        "and names the rule",
        refused_unverified.json().get("code") == "auth.email_not_verified",
    )

    other = browser.new_page()
    sign_in_as_booker(other)
    refused = other.request.post(
        f"{BASE}/api/bookings",
        data={
            "venueId": venue_id,
            "slots": [
                {"courtId": taken_court, "date": tomorrow.isoformat(), "hour": int(taken_hour)}
            ],
        },
    )
    check("an hour someone else holds is refused", refused.status == 409)
    check("and names the rule", refused.json().get("code") == "booking.slot_just_taken")

    # 8. The lead time is the server's, not the page's.
    too_soon = other.request.post(
        f"{BASE}/api/bookings",
        data={
            "venueId": venue_id,
            "slots": [
                {
                    "courtId": taken_court,
                    "date": venue_today().isoformat(),
                    "hour": datetime.datetime.now(
                        datetime.timezone(datetime.timedelta(hours=7))
                    ).hour,
                }
            ],
        },
    )
    check("the hour that is running cannot be booked", too_soon.status == 400)
    check(
        "and names the rule",
        too_soon.json().get("code") == "booking.starts_too_soon",
    )

    browser.close()

check.summarise()
