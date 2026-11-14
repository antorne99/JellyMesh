"""Drives real jellyfin-web in headless Chromium through a scenario, logging what the browser does.

Usage: webplay.py <base> <token> <user id> <server id> <item id> <scenario> <capture prefix> [seconds]
Scenarios: play, seek, audio, subs, stop, resume, fail
All responses reaching the browser are appended to <capture prefix>.bin, URLs and headers to <capture prefix>.txt,
so the leak scan reads what really arrived.
"""
import json, sys, time
from playwright.sync_api import sync_playwright

BASE, TOKEN, USER, SERVER, ITEM, SCENARIO, CAP = sys.argv[1:8]
SECONDS = int(sys.argv[8]) if len(sys.argv) > 8 else 12
import os
MAXBIT = os.environ.get('MAXBIT', '')
DEVICE = os.environ.get('DEVICE_ID', 'jm-play-browser')
CRED = {"Servers": [{"Id": SERVER, "Name": "p", "AccessToken": TOKEN, "UserId": USER, "ManualAddress": BASE,
                     "LocalAddress": BASE, "LastConnectionMode": 2, "manualAddressOnly": True,
                     "DateLastAccessed": int(time.time() * 1000)}]}
bin_out = open(CAP + ".bin", "ab")
txt_out = open(CAP + ".txt", "a")


def keep(u):
    return any(x in u for x in ("/JellyMesh/", "/videos/", "/Videos/", "PlaybackInfo", "/Sessions/Playing", "ActiveEncodings", ".m3u8", ".vtt"))


def state(pg):
    return pg.evaluate("""() => { const v=document.querySelector('video'); if(!v) return null;
      const tt=[...v.textTracks].map(t=>({k:t.kind,l:t.language,m:t.mode,c:t.cues?t.cues.length:null}));
      const b=[]; for(let i=0;i<v.buffered.length;i++) b.push([+v.buffered.start(i).toFixed(1),+v.buffered.end(i).toFixed(1)]);
      return {src:v.currentSrc.replace(/ApiKey=[^&]*/,'ApiKey=***').slice(0,130),t:+v.currentTime.toFixed(2),rs:v.readyState,paused:v.paused,dur:v.duration,err:v.error&&v.error.message,b,tt}; }""")


with sync_playwright() as p:
    b = p.chromium.launch(args=["--autoplay-policy=no-user-gesture-required"])
    c = b.new_context(locale="it-IT", viewport={"width": 1280, "height": 720})
    c.add_init_script("localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CRED)) + ");localStorage.setItem('enableAutoLogin','true');localStorage.setItem('_deviceId2'," + json.dumps(DEVICE) + ");" + ''.join(f"localStorage.setItem('enableautobitratebitrate-Video-{n}','false');localStorage.setItem('maxbitrate-Video-{n}','{MAXBIT}');" for n in ('true','false') if MAXBIT))
    pg = c.new_page()
    posts = []

    def on_req(r):
        if keep(r.url):
            print("REQ", r.method, r.url.replace(TOKEN, "***")[:170], flush=True)
            if r.method == "POST" and r.post_data and "PlaybackInfo" in r.url:
                d = json.loads(r.post_data)
                posts.append({k: v for k, v in d.items() if k != "DeviceProfile"})

    def on_res(r):
        u = r.url
        if "/web/" in u:
            return
        if keep(u):
            print("RES", r.status, u.replace(TOKEN, "***").split("?")[0][:120], flush=True)
        try:
            txt_out.write(f"{r.status} {u}\n{json.dumps(dict(r.headers))}\n")
            body = r.body()
            bin_out.write(body)
        except Exception:
            pass

    pg.on("request", on_req)
    pg.on("response", on_res)
    pg.on("console", lambda m: m.type in ("error", "warning") and ("layback" in m.text or "hls" in m.text.lower()) and print("CON", m.text[:200], flush=True))
    pg.on("pageerror", lambda e: print("PAGEERR", str(e)[:200], flush=True))
    pg.goto(f"{BASE}/web/index.html#/details?id={ITEM}&serverId={SERVER}")
    pg.wait_for_selector(".btnPlay:not(.hide)", timeout=30000)
    pg.wait_for_timeout(6000 if SCENARIO == "fail" else 1500)
    t0 = time.time()
    pg.locator(".btnPlay:not(.hide)").first.click()

    def wait_time(limit, ge=0.5):
        end = time.time() + limit
        while time.time() < end:
            s = state(pg)
            if s and s["t"] >= ge and s["rs"] >= 2:
                return s
            pg.wait_for_timeout(250)
        return state(pg)

    def dialogs():
        return pg.evaluate("""() => [...document.querySelectorAll('.dialog, .toast')].map(e=>e.innerText.trim().replace(/\\s+/g,' ').slice(0,160))""")

    if SCENARIO == "fail":
        seen = {}
        end = time.time() + SECONDS
        while time.time() < end:
            for d in pg.evaluate("""() => [...document.querySelectorAll('.dialog, .toast, .toast-container *')].map(e=>e.innerText.trim().replace(/\\s+/g,' ').slice(0,120))"""):
                if d and d not in seen:
                    seen[d] = round(time.time() - t0, 1)
                    pg.screenshot(path=CAP + "-toast.jpg")
            pg.wait_for_timeout(200)
        print("SEEN", seen)
        print("STATE", state(pg))
        print("DIALOGS", dialogs())
        pg.screenshot(path=CAP + ".jpg")
        print("URL", pg.url.split("#")[-1][:80])
    else:
        s = wait_time(SECONDS + 40)
        print(f"STARTED after {time.time()-t0:.1f}s", s, flush=True)
        pg.wait_for_timeout(4000)
        print("PROGRESS", state(pg), flush=True)
        pg.screenshot(path=CAP + "-play.jpg")
        if SCENARIO == "seek":
            for target in (int(state(pg)["dur"] * 0.66), int(state(pg)["dur"] * 0.2)):
                pg.evaluate("t => { document.querySelector('video').currentTime = t; }", target)
                s0 = time.time()
                r = None
                while time.time() - s0 < 45:
                    r = state(pg)
                    if r and r["t"] >= target and r["rs"] >= 2:
                        break
                    pg.wait_for_timeout(250)
                pg.wait_for_timeout(2500)
                print(f"SEEK {target} took {time.time()-s0-2.5:.1f}s", state(pg), flush=True)
        if SCENARIO in ("audio", "subs"):
            # The real menu of the OSD, as a user would do it.
            pg.mouse.move(600, 400)
            pg.wait_for_timeout(600)
            which = ".btnAudio" if SCENARIO == "audio" else ".btnSubtitles"
            pg.mouse.move(610, 410)
            pg.locator(which).first.click(timeout=8000, force=True)
            pg.wait_for_timeout(1000)
            opts = pg.evaluate("""() => [...document.querySelectorAll('.actionSheetMenuItem')].map(e=>e.innerText.trim().replace(/\\s+/g,' '))""")
            print("MENU", opts, flush=True)
            items = pg.locator(".actionSheetMenuItem")
            n = items.count()
            pick = int(sys.argv[9]) if len(sys.argv) > 9 else n - 1
            print("PICK", opts[pick] if opts else None, flush=True)
            items.nth(pick).click(force=True)
            pg.wait_for_timeout((SECONDS + 15) * 1000)
            print("AFTER", state(pg), flush=True)
            pg.screenshot(path=CAP + "-after.jpg")
        if SCENARIO == "resume":
            pg.evaluate("() => document.querySelector('video').pause()")
            print("PAUSED", state(pg), flush=True)
            pg.wait_for_timeout(SECONDS * 1000)
            pg.evaluate("() => document.querySelector('video').play()")
            pg.wait_for_timeout(12000)
            print("RESUMED", state(pg), flush=True)
        print("POSTS", posts, flush=True)
        print("DIALOGS", dialogs(), flush=True)
        # A real stop: the OSD back button, as a user leaves the player.
        pg.evaluate("() => { window.history.back(); }")
        pg.wait_for_timeout(5000)
        print("STOPPED url", pg.url.split("#")[-1][:80], flush=True)
    b.close()
