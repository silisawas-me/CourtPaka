"""The app installs as a PWA (PRD 8 Usability), and its service worker stays out of /api."""

from harness import BASE, Checks
from playwright.sync_api import sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    context = browser.new_context()
    page = context.new_page()
    page.goto(f"{BASE}/")

    manifest = page.evaluate(
        "document.querySelector('link[rel=manifest]')?.getAttribute('href')")
    check("the page names its manifest", manifest is not None)

    served = page.request.get(f"{BASE}/manifest.webmanifest").json()
    check("the manifest is the app's, in Thai, with the brand colour",
          served["name"] == "badPaka" and served["lang"] == "th"
          and served["theme_color"] == "#2d7643")
    sizes = {icon["sizes"] for icon in served["icons"]}
    check("with the icon sizes installing needs", {"192x192", "512x512"} <= sizes)
    for icon in served["icons"]:
        if page.request.get(f"{BASE}/{icon['src']}").status != 200:
            check(f"icon {icon['src']} is served", False)

    # The worker registers once the app is stable (or after 30 s at the latest).
    registered = page.evaluate("""async () => {
        const deadline = Date.now() + 40000;
        while (Date.now() < deadline) {
            const registration = await navigator.serviceWorker.getRegistration();
            if (registration?.active) return true;
            await new Promise(resolve => setTimeout(resolve, 500));
        }
        return false;
    }""")
    check("the service worker registers and activates", registered)

    # Controlled by the worker now: a navigation to the API must still reach the API, not be
    # answered with the app's index.html from the cache.
    page.reload()
    controlled = page.evaluate("navigator.serviceWorker.controller !== null")
    check("the page is served under the worker", controlled)
    api = page.goto(f"{BASE}/api/health/live")
    check("a navigation to /api goes to the API, not the app shell",
          api is not None and api.status == 200 and "<app-root" not in api.text())
    page.screenshot(path=str(check.shots / "api-through-the-worker.png"))

    browser.close()

check.summarise()
