"""Nothing on a phone is out of reach (PRD 8: mobile-first).

A grid item is as wide as its widest row unless it is told `min-width: 0`, so a page with a wide
table in it quietly grows past the window — and because the body clips the overflow rather than
scrolling it, whatever is out there cannot be reached at all.
"""

import sys

sys.path.insert(0, "C:/repo/scripts/verify")
from harness import BASE, OWNER, Checks, seeded_venue_id, sign_in
from playwright.sync_api import sync_playwright

check = Checks(__file__)


WIDER = """(width) => {
    const out = [];
    for (const el of document.querySelectorAll('body *')) {
      const box = el.getBoundingClientRect();
      if (box.width <= width + 1) continue;
      // Only the outermost offender: its children are wide because it is.
      const parent = el.parentElement;
      if (parent && parent.getBoundingClientRect().width > width + 1) continue;
      // An SVG's insides are drawn to its own viewBox and clipped by it; they are not layout.
      if (el.ownerSVGElement) continue;

      // Wide is fine if something above it scrolls — that is what a scroller is for.
      let reachable = false;
      for (let node = el; node; node = node.parentElement) {
        const how = getComputedStyle(node).overflowX;
        if (how === 'auto' || how === 'scroll') { reachable = true; break; }
      }

      out.push({
        what: el.tagName.toLowerCase() + (el.className ? '.' + String(el.className).split(' ')[0] : ''),
        width: Math.round(box.width),
        reachable,
      });
    }
    return out;
}"""

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    width = 390
    found = []

    # Signed out: the two doors, and the sign-in behind each.
    visitor = browser.new_page(viewport={"width": width, "height": 844})
    for name, path in (
        ("home", "/"),
        ("admin sign-in", "/login?as=admin"),
        ("staff sign-in", "/login?as=staff"),
    ):
        visitor.goto(BASE + path)
        visitor.wait_for_timeout(1200)
        found += [(name, one) for one in visitor.evaluate(WIDER, width)]
    visitor.close()

    staff = browser.new_page(viewport={"width": width, "height": 844})
    sign_in(staff, OWNER)
    for name, path in (
        ("venue", f"/venues/{venue_id}"),
        ("now", f"/venues/{venue_id}/now"),
        ("timeline", f"/venues/{venue_id}/timeline"),
        ("pricing", f"/venues/{venue_id}/pricing"),
        ("members", f"/venues/{venue_id}/packages"),
        ("today", f"/venues/{venue_id}/bookings"),
        ("slip queue", f"/venues/{venue_id}/slip-queue"),
        ("money", f"/venues/{venue_id}/money"),
        ("dashboard", f"/venues/{venue_id}/dashboard"),
        ("report", f"/venues/{venue_id}/report"),
        ("package board", f"/venues/{venue_id}/package-board"),
        ("settings", f"/venues/{venue_id}/settings"),
        ("closures", f"/venues/{venue_id}/closures"),
        ("my venues", "/venues"),
        ("account", "/account"),
    ):
        staff.goto(BASE + path)
        staff.wait_for_timeout(1600)
        found += [(name, one) for one in staff.evaluate(WIDER, width)]
    staff.close()
    browser.close()

# A scroller is allowed to be wider than the window; that is what scrolling is for.
out_of_reach = [(where, one) for where, one in found if not one["reachable"]]

for where, one in out_of_reach:
    print(f"    {one['width']:>5}px on a {width}px phone — {where}: {one['what']}")

check(
    "nothing on any screen is wider than the phone with no way to reach it",
    not out_of_reach,
)

check.summarise()
