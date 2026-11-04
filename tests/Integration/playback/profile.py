"""Captures the device profile jellyfin-web sends, and the codecs the browser decodes."""
import json, sys, time
from playwright.sync_api import sync_playwright
BASE, TOKEN, USER, SERVER, ITEM, OUT = sys.argv[1:7]
CRED = {"Servers": [{"Id": SERVER, "Name": "p", "AccessToken": TOKEN, "UserId": USER, "ManualAddress": BASE,
                     "LocalAddress": BASE, "LastConnectionMode": 2, "manualAddressOnly": True,
                     "DateLastAccessed": int(time.time() * 1000)}]}
with sync_playwright() as p:
    b = p.chromium.launch()
    c = b.new_context()
    c.add_init_script("localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CRED)) + ");localStorage.setItem('enableAutoLogin','true');")
    pg = c.new_page()
    def req(r):
        if "PlaybackInfo" in r.url and r.method == "POST":
            print("POST", r.url[:200])
            open(OUT, "w").write(r.post_data or "")
    pg.on("request", req)
    pg.goto(f"{BASE}/web/index.html#/details?id={ITEM}&serverId={SERVER}")
    pg.wait_for_selector(".btnPlay:not(.hide)", timeout=30000)
    pg.wait_for_timeout(1500)
    print(pg.evaluate("""() => { const v=document.createElement('video'); const o={};
      for (const [k,t] of Object.entries({h264:'video/mp4; codecs="avc1.640028"',hevc:'video/mp4; codecs="hvc1.1.6.L120.90"',
        vp9mp4:'video/mp4; codecs="vp09.00.10.08"',vp9webm:'video/webm; codecs="vp9"',av1mp4:'video/mp4; codecs="av01.0.08M.08"',
        av1webm:'video/webm; codecs="av01.0.08M.08"',aac:'audio/mp4; codecs="mp4a.40.2"',mp3:'audio/mpeg',opus:'audio/ogg; codecs="opus"',flac:'audio/flac',
        opusmp4:'audio/mp4; codecs="opus"', flacmp4:'audio/mp4; codecs="flac"'})) o[k]=v.canPlayType(t);
      const m={}; for (const t of ['video/mp4; codecs="av01.0.08M.08"','video/mp4; codecs="vp09.00.10.08"','audio/mp4; codecs="opus"','audio/mp4; codecs="flac"','video/mp4; codecs="avc1.640028"','audio/mp4; codecs="mp4a.40.2"']) m[t]=window.MediaSource?MediaSource.isTypeSupported(t):null;
      return {o,m}; }"""))
    pg.locator(".btnPlay:not(.hide)").first.click()
    pg.wait_for_timeout(4000)
    b.close()
