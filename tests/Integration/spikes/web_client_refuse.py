"""Drives the real jellyfin-web through the point 6 refusal cases, headless.

Usage: web_client_refuse.py <config json> <output folder>
Runs in the Playwright image, in the network namespace of the Jellyfin container.
Changes the probe mode over HTTP between cases, so one browser covers them all.
jellyfin-web draws its own dialogs: page.on("dialog") would never fire.
"""

import json
import re
import sys
import time

from playwright.sync_api import sync_playwright

CONFIG = json.load(open(sys.argv[1], encoding="utf-8"))
OUT = sys.argv[2]
BASE, TOKEN, USER, SERVER = CONFIG["base"], CONFIG["token"], CONFIG["user"], CONFIG["server"]
DEVICE = CONFIG["device"]
CREDENTIALS = {"Servers": [{
    "Id": SERVER, "Name": "probe", "AccessToken": TOKEN, "UserId": USER,
    "ManualAddress": BASE, "LocalAddress": BASE, "LastConnectionMode": 2,
    "manualAddressOnly": True, "DateLastAccessed": int(time.time() * 1000),
}]}

STATE = """() => {
  const visible = e => !!(e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden');
  const clean = s => (s || '').replace(/\\s+/g, ' ').trim();
  const v = document.querySelector('video');
  return {
    hash: location.hash.split('?')[0],
    dialogs: [...document.querySelectorAll('.dialogContainer .dialog')].filter(visible).map(d => clean(d.innerText)),
    toasts: [...document.querySelectorAll('.toast')].map(t => clean(t.textContent)),
    spinner: !!document.querySelector('.docspinner.mdlSpinnerActive'),
    video: v ? {ready: v.readyState, t: Math.floor(v.currentTime), err: v.error && v.error.code} : null,
    playButton: [...document.querySelectorAll('.btnPlay')].some(visible),
  };
}"""

TOP = """() => { const e = document.elementFromPoint(480, 300);
  return e ? e.tagName + '.' + String(e.className).slice(0, 60) : null; }"""

DETAILS = """() => {
  const clean = s => (s || '').replace(/\\s+/g, ' ').trim();
  const page = document.querySelector('.page:not(.hide) .detailPageContent') ? document.querySelector('.page:not(.hide)') : document;
  return {
    title: clean((page.querySelector('.itemName') || {}).innerText),
    overview: clean((page.querySelector('.overview') || {}).innerText),
    tags: clean((page.querySelector('.itemTags') || {}).innerText),
    misc: clean((page.querySelector('.itemMiscInfo') || {}).innerText),
  };
}"""

CARDS = """() => {
  const clean = s => (s || '').replace(/\\s+/g, ' ').trim();
  const rows = [...document.querySelectorAll('.page:not(.hide) .card, .page:not(.hide) .listItem')];
  return rows.map(c => ({
    text: clean(c.innerText).slice(0, 90),
    missing: !!c.querySelector('.missingIndicator, .indicator.missing'),
    indicators: [...c.querySelectorAll('[class*=ndicator]')].map(i => clean(i.innerText)).filter(Boolean),
    playOverlay: !!c.querySelector('button[data-action=resume], button[data-action=play]'),
  }));
}"""


def keep(url):
    return any(k in url for k in ("PlaybackInfo", "/stream", "/JellyMesh/Play", "/JellyMesh/Hls",
                                  "m3u8", "/Sessions/Playing"))


def short(url):
    return re.sub(r"[0-9a-f]{32}", "<id>", url.split("?")[0].replace(BASE, ""))


def list_names(response):
    """Names in a list response, to tell a client that ignores a field from a filter that missed."""
    if "/Items" not in response.url or "PlaybackInfo" in response.url or response.status != 200:
        return
    try:
        body = response.json()
    except Exception:
        return
    items = body.get("Items") if isinstance(body, dict) else body
    if items and isinstance(items, list) and isinstance(items[0], dict) and "Name" in items[0]:
        print("  list", short(response.url), [i.get("Name") for i in items][:4], "fields", sorted(items[0])[:4])


def headers():
    return {"Authorization": f'MediaBrowser Client="JellyMeshSpike", Device="p6", DeviceId="{DEVICE}", Version="0.1", Token="{TOKEN}"',
            "Accept": 'application/json; profile="PascalCase"'}


def user_data(context, item):
    """Played and PlayCount as the server holds them, to catch a failure counted as a view."""
    reply = context.request.get(f"{BASE}/Items/{item}", headers=headers())
    data = reply.json().get("UserData", {})
    return {"Played": data.get("Played"), "PlayCount": data.get("PlayCount"), "Ticks": data.get("PlaybackPositionTicks")}


def set_mode(context, case):
    query = {"refuse": case.get("refuse", ""), "item": case.get("item", "")}
    context.request.post(f"{BASE}/JellyMesh/ProbeMode", params=query)


def observe(page, seconds, seen, stamp, case_id=None):
    """Samples the page and prints each state once, with the time it first appeared."""
    start = time.time()
    last = None
    flaps = 0
    shot = False
    while time.time() - start < seconds:
        try:
            state = page.evaluate(STATE)
        except Exception as error:  # a navigation can replace the context mid-sample
            state = {"evaluate": str(error)[:60]}
        same = last is not None and {k: v for k, v in state.items() if k != "video"} == {k: v for k, v in last.items() if k != "video"}
        same = same and bool(state.get("video")) == bool(last.get("video"))
        if not same or (last and state.get("video") and state["video"]["t"] != (last.get("video") or {}).get("t")):
            print(f"  +{time.time() - stamp:5.1f}s {json.dumps(state, ensure_ascii=False)}")
        elif state != last:
            flaps += 1
        last = state
        # The toast lasts under 4 seconds, so catch what the user sees while it is up.
        if case_id and state.get("toasts") and not shot:
            page.wait_for_timeout(500)
            page.screenshot(path=f"{OUT}/{case_id}-toast.jpg", type="jpeg", quality=45)
            shot = True
        seen.update(d for d in state.get("dialogs", []))
        page.wait_for_timeout(250)
    if flaps:
        print(f"  video element reset or errored {flaps} times without a visible change")
    return last


def run_case(context, case):
    print(f"\n=== {case['id']} refuse='{case.get('refuse', '')}' item='{case.get('item', '')}'")
    set_mode(context, case)
    page = context.new_page()
    requests = []
    page.on("response", lambda r: keep(r.url) and requests.append(f"{r.request.method} {short(r.url)} -> {r.status}"))
    page.on("requestfailed", lambda r: keep(r.url) and requests.append(f"{r.method} {short(r.url)} -> FAILED {r.failure}"))
    page.on("pageerror", lambda e: print("  PAGEERROR", str(e)[:160]))
    item = CONFIG["items"][case.get("target", "offline")]

    for name, url in case.get("views", {}).items():
        view = context.new_page()
        view.on("response", lambda r: list_names(r))
        view.goto(f"{BASE}/web/index.html#{url}")
        view.wait_for_timeout(3500)
        print("  view", name, json.dumps(view.evaluate(CARDS), ensure_ascii=False))
        view.screenshot(path=f"{OUT}/{case['id']}-{name}.jpg", type="jpeg", quality=45)
        view.close()

    # Play on a series resumes at the next unwatched episode, so forget earlier cases.
    if case.get("seek"):
        for episode in CONFIG["episodes"]:
            context.request.delete(f"{BASE}/UserPlayedItems/{episode}", headers=headers())
    before = user_data(context, item)
    page.goto(f"{BASE}/web/index.html#/home")
    page.wait_for_timeout(1500)
    page.evaluate("(h) => { location.hash = h; }", f"/details?id={item}&serverId={SERVER}")
    try:
        page.wait_for_selector(".itemName", timeout=20000)
    except Exception:
        print("  details page never rendered")
    page.wait_for_timeout(1800)
    print("  details", json.dumps(page.evaluate(DETAILS), ensure_ascii=False))
    state = page.evaluate(STATE)
    print("  before", json.dumps(state, ensure_ascii=False))
    page.screenshot(path=f"{OUT}/{case['id']}-details.jpg", type="jpeg", quality=45)
    if not state["playButton"]:
        print("  no play button on the details page")
        page.close()
        return

    stamp = time.time()
    seen = set()
    page.locator(".btnPlay:visible").first.click()
    if case.get("seek"):
        page.wait_for_function("() => { const v = document.querySelector('video'); return v && v.readyState >= 2 && v.currentTime > 0.5; }", timeout=20000)
        print("  first episode playing, seeking to the end")
        page.evaluate("() => { const v = document.querySelector('video'); v.currentTime = v.duration - 0.7; }")
    observe(page, case.get("wait", 7), seen, stamp, case["id"])
    page.screenshot(path=f"{OUT}/{case['id']}-after.jpg", type="jpeg", quality=45)

    print("  requests before dismiss:")
    for line, count in sorted({r: requests.count(r) for r in requests}.items()):
        print(f"    {count}x {line}")

    for _ in range(4):
        buttons = page.locator(".dialogContainer .dialog button:visible")
        if buttons.count() == 0:
            break
        # Dialogs stack, and only the last one opened takes clicks.
        print(f"  dismiss top of {buttons.count()} with", buttons.last.inner_text())
        buttons.last.click(timeout=4000)
        page.wait_for_timeout(700)
    final = page.evaluate(STATE)
    print("  after dismiss", json.dumps(final, ensure_ascii=False))
    if final["video"] or final["spinner"] or "video" in final["hash"]:
        sent = len(requests)
        page.go_back()
        page.wait_for_timeout(3000)
        print("  after back", json.dumps(page.evaluate(STATE), ensure_ascii=False), f"requests in 3s: {len(requests) - sent}")
        page.screenshot(path=f"{OUT}/{case['id']}-back.jpg", type="jpeg", quality=45)

    # Is the player left in a state that blocks the next title?
    if case.get("stuck"):
        print("  under the centre of the page:", page.evaluate(TOP))
        online = CONFIG["items"]["online"]
        page.evaluate("(h) => { location.hash = h; }", f"/details?id={online}&serverId={SERVER}")
        page.wait_for_timeout(2500)
        print("  on another title, centre of the page:", page.evaluate(TOP))
        try:
            page.locator(".btnPlay:visible").first.click(timeout=4000)
            page.wait_for_timeout(3000)
            print("  second title by click", json.dumps(page.evaluate(STATE), ensure_ascii=False))
        except Exception as error:
            print("  second title cannot be clicked:", str(error).splitlines()[0][:80])
            page.reload()
            page.wait_for_selector(".itemName", timeout=20000)
            page.wait_for_timeout(1800)
            page.locator(".btnPlay:visible").first.click(timeout=4000)
            page.wait_for_timeout(3000)
            print("  after a page reload", json.dumps(page.evaluate(STATE), ensure_ascii=False))
        print("  user data", json.dumps(before), "->", json.dumps(user_data(context, item)))
        page.close()
        return

    # Can the user try again? Only counts when the first attempt was refused.
    if not case.get("noretry"):
        count = len([r for r in requests if "PlaybackInfo" in r])
        again = page.evaluate(STATE)
        if again["playButton"] and not again["dialogs"]:
            page.locator(".btnPlay:visible").first.click()
            page.wait_for_timeout(2000)
            more = len([r for r in requests if "PlaybackInfo" in r]) - count
            print(f"  second click: {more} more PlaybackInfo", json.dumps(page.evaluate(STATE), ensure_ascii=False))
    print("  user data", json.dumps(before), "->", json.dumps(user_data(context, item)))
    page.close()


with sync_playwright() as p:
    browser = p.chromium.launch(args=["--autoplay-policy=no-user-gesture-required"])
    selected = sys.argv[3:] or None
    for case in CONFIG["cases"]:
        if selected and not any(case["id"].startswith(s) for s in selected):
            continue
        # A fresh context per case: a list view or a broken player cannot carry over.
        context = browser.new_context(locale=CONFIG.get("locale", "it-IT"), viewport={"width": 960, "height": 540})
        context.add_init_script(
            "localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CREDENTIALS)) + ");"
            "localStorage.setItem('enableAutoLogin', 'true');"
            "localStorage.setItem('_deviceId2', " + json.dumps(DEVICE) + ");")
        try:
            run_case(context, case)
        except Exception as error:
            print("  CASE FAILED", str(error)[:200])
        context.close()
    browser.close()
