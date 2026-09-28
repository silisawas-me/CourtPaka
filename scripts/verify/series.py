"""The group that comes at the same hour every week (US-30).

What is checked is the thing the venue actually gets: an arrangement written down once, weeks that
appear on the day's list without anybody booking them, a week somebody else already has reported
rather than taken, and the whole thing stood down again with its weeks given back.

The weeks are made by the caretaker, not by the screen, so the checks below wait for a sweep.
"""

import datetime
import time

from harness import (
    open_more,
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    open_seeded_venue,
    pick_date,
    seeded_venue_id,
    sign_in,
    stop_every_series,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)

# Far enough out that the scripts working on tomorrow are nowhere near it, and inside the thirty
# days a week can be made in. The hour is the last of the evening for the same reason.
FIRST_WEEK = venue_today() + datetime.timedelta(days=21)
DAY = FIRST_WEEK.strftime("%A")
FROM_HOUR = 21
UNTIL_HOUR = 22

# The caretaker sweeps every ten seconds locally; three sweeps is patience enough to say it is
# not coming.
SWEEPS = 40


def standing(page, venue_id):
    """The venue's arrangements, read through the API — this is setup and waiting, not the check."""
    return page.request.get(f"{BASE}/api/venues/{venue_id}/series").json()


def wait_for(page, venue_id, series_id, answered):
    """Waits for the sweep to have answered this arrangement the way `answered` asks, and hands
    back the arrangement as it then stands — or None, once waiting is no longer patience."""
    for _ in range(SWEEPS):
        for one in standing(page, venue_id):
            if one["seriesId"] == series_id and answered(one):
                return one
        time.sleep(1)

    return None


def agree(page, court_id, name, from_hour, until_hour, first_week):
    """Fills in the form on screen, which is the thing being checked.

    The first week is picked rather than left at today's date: a group that starts today has five
    weeks inside the booking window, and a script that books a month of somebody else's floor is
    a script the next one has to work around."""
    page.click(f"[data-testid=court-{court_id}]")
    page.click(f"[data-testid=day-{first_week.strftime('%A')}]")
    page.fill("[data-testid=from-hour]", str(from_hour))
    page.fill("[data-testid=until-hour]", str(until_hour))
    page.fill("[data-testid=customer-name]", name)
    pick_date(page, first_week, toggle="[data-testid=starts-on-toggle] button")
    page.click("[data-testid=save-series]")


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    stop_every_series(page, venue_id)

    page.goto(f"{BASE}{open_seeded_venue(page)}")
    open_more(page)
    page.click("[data-testid=nav-series]")
    page.wait_for_selector("[data-testid=save-series]")
    check("the venue has a door for the groups that come every week", True, page)

    # 1. Written down once, and the weeks appear on their own.
    court_id = page.request.get(f"{BASE}/api/venues/{venue_id}/courts").json()[0]["id"]
    agree(page, court_id, "ก๊วนตรวจสอบ", FROM_HOUR, UNTIL_HOUR, FIRST_WEEK)
    page.wait_for_selector("[data-testid=series-list] li")

    agreed = standing(page, venue_id)[0]
    check("an arrangement can be written down in one screen",
          agreed["day"] == DAY and agreed["fromHour"] == FROM_HOUR, page)

    filled = wait_for(page, venue_id, agreed["seriesId"], lambda one: one["booked"] >= 1)
    check("the weeks are booked by themselves, without anybody asking", filled is not None)

    # And the booking is an ordinary one on the day's list, with the money still to take.
    # Whichever week it made: the arrangement runs until somebody stops it, so more than one of
    # its weeks is inside the window and which of them was free depends on what the run before
    # this one left behind. The day's list keeps what was cancelled too, so only what is still on
    # counts.
    weeks = [
        row
        for ahead in (0, 7)
        for row in page.request.get(
            f"{BASE}/api/venues/{venue_id}/bookings"
            f"?date={(FIRST_WEEK + datetime.timedelta(days=ahead)).isoformat()}").json()
        if row["customerName"] == "ก๊วนตรวจสอบ" and row["status"] == "Confirmed"
    ]
    check("the week is on the day's list as a booking the counter can take money for",
          len(weeks) >= 1 and all(row["can"]["takeMoney"] for row in weeks))

    page.reload()
    page.wait_for_selector("[data-testid=series-list] li")
    check("the venue can see how many weeks are booked", True, page)

    # 2. An hour somebody already has is reported, not taken.
    stop_every_series(page, venue_id)

    second_week = FIRST_WEEK + datetime.timedelta(days=7)

    # Sold across the counter, unless an earlier run of this script already sold it — a booking
    # made at a counter is never cancelled by this script, because cancelling it is not what is
    # being checked, and the hour being somebody else's is all the check needs.
    def held_by_a_walk_in() -> bool:
        return any(
            row["customerName"] == "คนเดินเข้ามา"
            and row["status"] in ("Confirmed", "Completed")
            and any(slot["hour"] == FROM_HOUR and slot["courtId"] == court_id
                    for slot in row["slots"])
            for row in page.request.get(
                f"{BASE}/api/venues/{venue_id}/bookings?date={second_week.isoformat()}").json()
        )

    if not held_by_a_walk_in():
        page.request.post(
            f"{BASE}/api/venues/{venue_id}/bookings",
            data={
                "slots": [
                    {"courtId": court_id, "date": second_week.isoformat(), "hour": FROM_HOUR}
                ],
                "customerName": "คนเดินเข้ามา",
                "customerPhone": None,
                "paidBy": "Cash",
            },
        )

    check("an hour of the group's slot is somebody else's first", held_by_a_walk_in())

    page.reload()
    page.wait_for_selector("[data-testid=save-series]")
    agree(page, court_id, "ก๊วนที่ชนกัน", FROM_HOUR, UNTIL_HOUR, FIRST_WEEK)
    page.wait_for_selector("[data-testid=series-list] li")

    clashing = standing(page, venue_id)[0]
    answered = wait_for(
        page,
        venue_id,
        clashing["seriesId"],
        lambda one: any(miss["date"] == second_week.isoformat() for miss in one["missed"]),
    )
    check("the week somebody already had is not taken from them", answered is not None)

    page.reload()
    page.wait_for_selector(f"[data-testid=missed-{clashing['seriesId']}]")
    missed = page.locator(f"[data-testid=missed-{clashing['seriesId']}]").inner_text()
    check("and the venue is told which week, in words rather than a code",
          "booking." not in missed and str(second_week.day) in missed, page)

    # The hour its owner has is still theirs.
    day = page.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={second_week.isoformat()}").json()
    check("the booking that was in the way is untouched",
          any(row["customerName"] == "คนเดินเข้ามา" and row["status"] == "Confirmed"
              for row in day))

    # 3. Changing it from today ends the old one and starts the one that took over.
    page.click(f"[data-testid=change-{clashing['seriesId']}]")
    page.fill("[data-testid=from-hour]", str(FROM_HOUR - 1))
    page.fill("[data-testid=until-hour]", str(FROM_HOUR))
    page.click("[data-testid=save-series]")
    # The count of what the change cancelled only appears once the server has answered, and the
    # list of ones that are over was already on screen — so waiting for that would prove nothing.
    expect(page.get_by_test_id("last-stop")).to_be_visible()

    after = standing(page, venue_id)
    took_over = [one for one in after if one["state"] == "Running"]
    check("changing it ends the old arrangement and starts one that took over",
          len(took_over) == 1
          and took_over[0]["fromHour"] == FROM_HOUR - 1
          and any(one["state"] == "Ended" for one in after),
          page)

    # 4. Stood down, and the weeks it had booked come back.
    running = took_over[0]
    wait_for(page, venue_id, running["seriesId"], lambda one: one["booked"] >= 1)
    page.reload()
    page.wait_for_selector(f"[data-testid=stop-{running['seriesId']}]")
    page.click(f"[data-testid=stop-{running['seriesId']}]")

    # Asked twice, because the answer cancels weeks of bookings and may owe money back.
    page.wait_for_selector(f"[data-testid=stopping-{running['seriesId']}]")
    check("stopping a group is asked twice before it happens", True, page)

    page.fill(f"[data-testid=stop-reason-{running['seriesId']}]", "ก๊วนเลิกเล่น")
    page.click(f"[data-testid=stop-confirm-{running['seriesId']}]")
    expect(page.get_by_test_id("last-stop")).to_be_visible()
    check("stopping the group says how many weeks it cancelled", True, page)

    # And the reason is kept with it, which is what somebody asks about afterwards.
    check("the reason it was stopped is kept",
          any(one["endReason"] == "ก๊วนเลิกเล่น" for one in standing(page, venue_id)))

    check("nothing is left standing",
          all(one["state"] == "Ended" for one in standing(page, venue_id)))

    # The screens a design review asks for, on an arrangement that is over rather than an empty
    # page: what the venue reads here is a list, and an empty list shows none of it.
    agree(page, court_id, "ก๊วนวันอังคาร", 20, 21, FIRST_WEEK)
    page.wait_for_selector("[data-testid=series-list] li")

    check("desk, Thai", True, page)
    page.set_viewport_size({"width": 390, "height": 844})
    check("phone, Thai", True, page)
    check("nothing on a phone scrolls the page sideways",
          page.evaluate("document.documentElement.scrollWidth") <= 390)

    if not page.get_by_test_id("language-en").is_visible():
        page.click("[data-testid=open-menu]")
    page.click("[data-testid=language-en]")
    expect(page.locator("html")).to_have_attribute("lang", "en")
    check("phone, English", True, page)
    page.set_viewport_size({"width": 1280, "height": 900})
    check("desk, English", True, page)

    # And the venue is left the way the other scripts expect to find it, including the language
    # the account is remembered on. On a desk a venue page hides the bar above, so the switch is
    # the one the sidebar carries (PRD US-25).
    stop_every_series(page, venue_id)
    page.click("[data-testid=top-language-th]")
    expect(page.locator("html")).to_have_attribute("lang", "th")

    page.close()
    browser.close()

check.summarise()
