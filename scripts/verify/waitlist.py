"""The queue for a day that has nothing left (US-27)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    new_booker,
    open_seeded_venue,
    pick_date,
    seeded_venue_id,
    sign_in,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
# Far enough ahead that nothing this suite books is in the way, and still inside the window.
day = venue_today() + datetime.timedelta(days=20)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    # A booker looks at a day and takes a place in the queue for it.
    page = browser.new_page(viewport={"width": 390, "height": 844})
    email = new_booker(page)
    sign_in(page, email)
    page.goto(f"{BASE}/book/{venue_id}?date={day.isoformat()}")
    page.wait_for_selector("[data-testid=waitlist]")
    check("the queue is offered under the day it is for", True, page)

    with page.expect_response(lambda r: r.url.endswith("/api/waitlist")) as joined:
        page.click("[data-testid=wait-for-it]")
    check("a place in the queue is taken", joined.value.status == 201)

    took = joined.value.json()
    grid = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={day.isoformat()}").json()
    check(
        "and it asks for the hours this venue actually sells that day",
        took["fromHour"] == grid["opensHour"] and took["untilHour"] == grid["closesHour"],
    )

    expect(page.locator("[data-testid=waiting-now]")).to_be_visible()
    check("the page then says they are in it rather than offering again", True, page)

    # Twice is not two places.
    again = page.request.post(
        f"{BASE}/api/waitlist",
        data={
            "venueId": venue_id,
            "date": day.isoformat(),
            "fromHour": grid["opensHour"],
            "untilHour": grid["closesHour"],
            "hours": 1,
        },
    )
    check("nobody stands in the same queue twice", again.status == 409)
    check(
        "and the refusal says which rule it is",
        again.json().get("code") == "waitlist.already_waiting",
    )

    # It is on their own page, with the way out of it.
    page.goto(f"{BASE}/bookings")
    page.wait_for_selector(f"[data-testid=waiting-{took['id']}]")
    check("the queue is on the booker's own page", True, page)

    # The counter sees who is waiting on that day.
    counter = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(counter, OWNER)
    counter.goto(f"{BASE}{open_seeded_venue(counter)}")
    counter.wait_for_selector("[data-testid=venue-bookings-link]")
    counter.click("[data-testid=venue-bookings-link]")
    counter.wait_for_selector("[data-testid=day]")
    pick_date(counter, day)
    counter.wait_for_selector(f"[data-testid=waiting-{took['id']}]")
    check(
        "the counter sees who wanted the day and how to reach them",
        email in counter.locator(f"[data-testid=waiting-{took['id']}]").inner_text(),
        counter,
    )

    # A queue carries addresses, so it is behind the permission the day's list is behind.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    refused = stranger.request.get(f"{BASE}/api/venues/{venue_id}/waitlist")
    check("a stranger cannot read the queue", refused.status == 403)

    # Standing down gives the place up, once.
    page.click(f"[data-testid=leave-{took['id']}]")
    page.wait_for_selector(f"[data-testid=waiting-{took['id']}]", state="detached")
    check("standing down gives the place up", True, page)

    left_again = page.request.delete(f"{BASE}/api/waitlist/{took['id']}")
    check("and it cannot be given up twice", left_again.status == 404)

    mine = page.request.get(f"{BASE}/api/waitlist").json()
    check("the queue is empty afterwards", all(one["id"] != took["id"] for one in mine))

    # The offer: hours that come back are put aside for whoever asked first, and the booker sees
    # a held booking like any other (US-27). The caretaker makes it; local sweeps every 10 s.
    grid_day = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={day.isoformat()}").json()
    free = next(
        (row["courtId"], cell["hour"])
        for row in grid_day["courts"]
        for cell in row["hours"]
        if cell["status"] == "Free"
    )

    waiter = browser.new_page()
    waiter_email = new_booker(waiter)
    sign_in(waiter, waiter_email)
    joined = waiter.request.post(
        f"{BASE}/api/waitlist",
        data={
            "venueId": venue_id,
            "date": day.isoformat(),
            "fromHour": free[1],
            "untilHour": free[1] + 1,
            "hours": 1,
        },
    )
    check("somebody waits for an hour the day still has", joined.status == 201)

    # Wait for one sweep of the caretaker to notice.
    offer = None
    for _ in range(30):
        waiter.wait_for_timeout(1000)
        mine = waiter.request.get(f"{BASE}/api/bookings").json()
        held = [one for one in mine["upcoming"] if one["status"] == "Held"]
        if held:
            offer = held[0]
            break

    check("the hours are put aside for them without anybody pressing anything", offer is not None)
    if offer is not None:
        check(
            "and it is an ordinary hold they can pay for",
            offer["totalBaht"] > 0 and offer["holdExpiresAt"] is not None,
        )

        place = waiter.request.get(f"{BASE}/api/waitlist").json()
        check(
            "while their place in the queue says it has been offered",
            any(one["state"] == "Offered" for one in place),
        )

    browser.close()

check.summarise()
