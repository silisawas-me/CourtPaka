# Browser checks

Playwright scripts that drive the real stack, for the flows unit tests cannot cover: what a page
shows after a reload, what the server actually stored, and what a member without the permission
sees.

```bash
docker compose --profile full up -d --build      # from the repo root
pip install playwright && playwright install chromium
python scripts/verify/venue_settings.py
```

They sign in as the seeded accounts (`owner@courtpaka.local` / `staff@courtpaka.local`,
`DevPassword1`) and write screenshots next to themselves. Each check prints PASS or FAIL and the
script exits non-zero if anything failed.

Three rules worth keeping.

1. Set up the state the script needs through the API before driving the UI. The scripts share one
   venue, so one that closes a weekday will break another that prices one, and the failure will
   look like an app bug.
2. Wait for the response before reloading. The pages apply a change straight away and reconcile
   with the server, so reloading right after a click cancels the request in flight and the check
   reads the old state.
3. Name what you are looking for. Count rows only once the section has rendered, and find a form by
   something inside it rather than by where it sits — the settings page has four now.

| Script | Covers |
|---|---|
| `venue_ui.py` | Sign-in redirects, venue detail, members and permissions (US-14) |
| `venue_settings.py` | Courts and opening hours (US-11) |
| `venue_pricing.py` | Prices and the cancellation policy (US-11) |
