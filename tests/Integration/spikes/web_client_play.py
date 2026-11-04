"""Plays one item in the real jellyfin-web, headless, and lists what the browser requests.

Usage: web_client_play.py <base url> <token> <user id> <server id> <item id> <seconds>
Runs inside the Playwright image, in the network namespace of the Jellyfin container.
"""

import json
import sys
import time

from playwright.sync_api import sync_playwright

BASE, TOKEN, USER, SERVER, ITEM, SECONDS = sys.argv[1:7]
CREDENTIALS = {"Servers": [{
    "Id": SERVER, "Name": "probe", "AccessToken": TOKEN, "UserId": USER,
    "ManualAddress": BASE, "LocalAddress": BASE, "LastConnectionMode": 2,
    "manualAddressOnly": True, "DateLastAccessed": int(time.time() * 1000),
}]}


def keep(url):
    return ("/JellyMesh/" in url or "/stream" in url or "master.m3u8" in url
            or "main.m3u8" in url or "PlaybackInfo" in url or "/hls1/" in url)


with sync_playwright() as p:
    browser = p.chromium.launch()
    context = browser.new_context()
    context.add_init_script(
        "localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CREDENTIALS)) + ");"
        "localStorage.setItem('enableAutoLogin', 'true');")
    page = context.new_page()
    page.on("request", lambda r: keep(r.url) and print("REQ", r.method, r.url.split("?")[0][:110],
                                                       "auth" if "ApiKey" in r.url or "authorization" in r.headers else "no-auth"))
    page.on("response", lambda r: keep(r.url) and print("RES", r.status, r.url.split("?")[0][:110]))
    page.on("console", lambda m: "layback" in m.text and print("CON", m.text[:200]))
    page.goto(f"{BASE}/web/index.html#/details?id={ITEM}&serverId={SERVER}")
    page.wait_for_selector(".btnPlay:not(.hide)", timeout=30000)
    page.wait_for_timeout(1500)
    print("codecs", page.evaluate("""() => { const v = document.createElement('video');
      return {h264: v.canPlayType('video/mp4; codecs="avc1.640028"'), vp9: v.canPlayType('video/webm; codecs="vp9"')}; }"""))
    page.locator(".btnPlay:not(.hide)").first.click()
    page.wait_for_timeout(int(SECONDS) * 1000)
    print("video src", page.evaluate("""() => { const v = document.querySelector('video');
      return v ? {src: v.currentSrc, readyState: v.readyState, time: v.currentTime, error: v.error && v.error.message} : null; }"""))
    browser.close()
