"""Checks of the configuration page in a real browser, at 1280 and 390 px.

Usage: pagecheck.py <scenario>...   Run it through check.sh, which puts the servers in the state each one expects.
"""
import re
import sys

import uipage as u

FAILED = [0]
WIDTHS = list(u.VIEWPORTS)
UNREACHABLE = "http://127.0.0.1:1"
PAIR = "JM Sonda Mobile"
CHAIN = "JM Catena Uno"
HOSTILE = "<b>x</b>"
HOSTILE_PEER = "<i>y</i>"


def check(name, passed, detail=""):
    """Prints one result."""
    print(("PASS " if passed else "FAIL ") + name + ("" if passed else ": " + str(detail)), flush=True)
    if not passed:
        FAILED[0] = 1


def text(page, selector):
    """Returns the text of a part of the page."""
    return page.locator(selector).inner_text()


def until(page, condition, seconds=15):
    """Waits for a condition while the page goes on, and returns whether it came true."""
    for _ in range(seconds * 10):
        if condition():
            return True
        page.wait_for_timeout(100)
    return False


def healthy(page, name):
    """Checks what must hold in every state: no script error, and no sideways scroll."""
    check(name + " has no script error", not page.errors, page.errors)
    sizes = page.evaluate("[document.documentElement.scrollWidth, innerWidth]")
    check(name + " does not scroll sideways", sizes[0] <= sizes[1], sizes)


def settings_match(page, server, name):
    """Checks that the settings on the page are the saved ones."""
    config = u.config(server)
    for selector, field in u.SETTINGS.items():
        shown = page.locator(selector).input_value()
        check(f"{name} shows {field}", shown == str(config[field]), f"{shown} against {config[field]}")
    check(name + " shows metadata protection", page.locator(u.METADATA_PROTECTION).is_checked() == config["MetadataProtection"])


def first(s, width):
    """A server with nothing configured."""
    name = width + " first run"
    page = s.open("first", width)
    check(name + " is active", "active" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " has no server rows", page.locator(u.SERVER).count() == 0)
    check(name + " has no warning", not page.locator(u.WARNING).is_visible())
    for field, selector in u.ADD_FIELDS.items():
        check(f"{name} offers the {field} field", page.locator(selector).is_visible())
    check(name + " can add a server", page.locator(u.ADD_SUBMIT).is_enabled())
    check(name + " has no folder yet", page.locator(u.FEDERATED_FOLDER).input_value() == "")
    check(name + " says what the folder needs", text(page, u.FOLDER_INFO) != "")
    check(name + " has no doubtful films", page.locator(u.MERGE_CASE).count() == 0)
    check(name + " has no merges", page.locator(u.MERGE).count() == 0)
    settings_match(page, "first", name)
    healthy(page, name)
    u.shot(page, f"first-{width}")
    page.context.close()


def first_nokey(s, width):
    """A server with nothing configured and no secret key."""
    name = width + " first run without a key"
    page = s.open("first", width)
    warning = text(page, u.WARNING) if page.locator(u.WARNING).is_visible() else ""
    check(name + " warns", "JELLYMESH_SECRET_KEY" in warning, warning)
    check(name + " is suspended", "suspended" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " cannot add a server", page.locator(u.ADD_SUBMIT).is_disabled())
    healthy(page, name)
    u.shot(page, f"first-nokey-{width}")
    page.context.close()


def normal(s, width):
    """Working servers, a synced catalog, doubtful films and a connected server."""
    name = width + " normal load"
    page = s.open("fresh", width)
    check(name + " is active", "active" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " has no warning", not page.locator(u.WARNING).is_visible())
    stock = u.server_row(page, "stock")
    peer = u.server_row(page, "peer")
    check(name + " lists the stock remote as standard", "standard" in stock.inner_text() and "online" in stock.inner_text(), stock.inner_text())
    check(name + " lists the peer as JellyMesh", "JellyMesh" in peer.inner_text() and "online" in peer.inner_text(), peer.inner_text())
    sync = u.api("fresh", "GET", "/JellyMesh/Admin/Sync")[1]
    catalog = text(page, u.SYNC_SERVERS)
    for server in sync["Servers"]:
        check(f"{name} counts the films of {server['ServerName']}", f"{server['ServerName']}: {server['Movies']} films" in catalog, catalog)
    check(name + " can sync", page.locator(u.SYNC_NOW).is_enabled())
    check(name + " lists the pair", u.merge_case(page, PAIR).count() == 1)
    check(name + " lists the chain", u.merge_case(page, CHAIN).count() == 1)
    connected = u.peer_row(page, "jellymesh")
    check(name + " lists the connected server", connected.count() == 1)
    check(name + " shows its stream limit", connected.locator(u.NUMBER).input_value() == "4")
    check(name + " shows what it shares", connected.locator(u.CHECKBOX + ":checked").count() == 2)
    check(name + " offers the free user", page.locator(u.PEERS + " select").locator("option", has_text="friend").count() == 1)
    settings_match(page, "fresh", name)
    healthy(page, name)
    u.shot(page, f"normal-{width}")
    page.context.close()


def loading(s, width):
    """A slow load shows that it is loading, and a failed one says so."""
    name = width + " loading"

    hold = []

    def slow(context):
        context.route("**/JellyMesh/Admin/Status", lambda route: hold.append(route))

    page = s.open("fresh", width, route=slow, wait=False)
    page.wait_for_selector(u.PAGE, state="attached", timeout=30000)
    page.wait_for_selector(u.LOADING, state="visible", timeout=15000)
    check(name + " shows the spinner while it waits", page.locator(u.LOADING).is_visible())
    u.shot(page, f"loading-{width}")
    until(page, lambda: hold)
    hold.pop().continue_()
    u.wait_loaded(page)
    check(name + " ends with the data", page.locator(u.SERVER).count() == 2)
    healthy(page, name)
    page.context.close()

    name = width + " failed load"

    def broken(context):
        context.route("**/JellyMesh/Admin/Status", lambda route: route.fulfill(status=500, json={}))

    page = s.open("fresh", width, route=broken, wait=False)
    shown = u.wait_message(page)
    check(name + " says so", shown != "", shown)
    page.wait_for_function("s => document.querySelector(s).value !== ''", arg=u.SYNC_INTERVAL, timeout=15000)
    check(name + " still shows the settings", True)
    healthy(page, name)
    u.shot(page, f"load-failed-{width}")
    page.context.close()


def errors(s, width):
    """Actions the server refuses: each says why, and changes nothing."""
    name = width + " errors"
    page = s.open("fresh", width)
    servers = len(u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"])

    page.locator(u.ADD_FIELDS["name"]).fill("nowhere")
    page.locator(u.ADD_FIELDS["url"]).fill(UNREACHABLE)
    page.locator(u.ADD_FIELDS["user"]).fill("jellymesh")
    page.locator(u.ADD_FIELDS["password"]).fill("not-the-password")
    page.locator(u.ADD_SUBMIT).click()
    shown = u.wait_message(page)
    check(name + " an unreachable server says so", "cannot be reached" in shown, shown)
    check(name + " an unreachable server is not added", len(u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"]) == servers)
    check(name + " the password does not stay in the form", page.locator(u.ADD_FIELDS["password"]).input_value() == "")
    check(name + " the rest of the form stays", page.locator(u.ADD_FIELDS["url"]).input_value() == UNREACHABLE)
    u.shot(page, f"error-add-{width}")
    for selector in u.ADD_FIELDS.values():
        page.locator(selector).fill("")

    u.clear_message(page)
    case = u.merge_case(page, PAIR)
    case.locator(u.CHECKBOX).first.check()
    u.button(case, "merge").click()
    shown = u.wait_message(page)
    check(name + " one title cannot be merged", "at least two" in shown, shown)
    check(name + " nothing was merged", u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]["Merges"] == [])
    u.shot(page, f"error-merge-{width}")

    u.clear_message(page)
    folder = u.config("fresh")["FederatedFolderPath"]
    page.locator(u.FEDERATED_FOLDER).fill("relative/path")
    page.locator(u.SAVE_SETTINGS).click()
    shown = u.wait_message(page)
    check(name + " a refused save says so", "refused" in shown, shown)
    check(name + " a refused save keeps the folder", u.config("fresh")["FederatedFolderPath"] == folder)
    u.shot(page, f"error-settings-{width}")
    page.locator(u.FEDERATED_FOLDER).fill(folder)

    page.locator(u.SYNC_INTERVAL).fill("60")
    check(name + " the browser stops a low interval", not page.locator(u.SETTINGS_FORM).evaluate("form => form.checkValidity()"))
    healthy(page, name)
    page.context.close()


def keyboard(s, width):
    """Every main action can be reached with Tab, in order, and used from the keyboard."""
    name = width + " keyboard"
    page = s.open("fresh", width)
    reached = []
    for _ in range(400):
        page.keyboard.press("Tab")
        focus = page.evaluate("""p => { const e = document.activeElement;
          return e.closest(p) ? (e.id || (e.textContent || '').trim() || e.type) : null; }""", u.PAGE)
        if focus is None and reached:
            break
        if focus is not None:
            reached.append(focus)

    for action in [u.BUTTON["test"], u.BUTTON["libraries"], u.BUTTON["remove"], u.BUTTON["merge"], u.BUTTON["connect"], u.BUTTON["save"],
                   u.SYNC_NOW[1:], u.ADD_SUBMIT[1:], u.SYNC_INTERVAL[1:], u.ADD_FIELDS["name"][1:], u.METADATA_PROTECTION[1:]]:
        check(f"{name} reaches {action}", action in reached, reached)
    check(name + " reaches a settings Save", reached.count(u.BUTTON["save"]) >= 2, reached)
    check(name + " reaches the title checkboxes", reached.count("checkbox") >= 5, reached)

    libraries = u.button(u.server_row(page, "stock"), "libraries")
    libraries.focus()
    page.keyboard.press("Enter")
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    check(name + " Enter opens the libraries", u.server_row(page, "stock").locator(u.CHECKBOX).count() == 2)
    box = u.merge_case(page, PAIR).locator(u.CHECKBOX).first
    box.focus()
    page.keyboard.press("Space")
    check(name + " Space selects a title", box.is_checked())
    healthy(page, name)
    page.context.close()


CONTRAST = """s => {
  const parse = c => { const m = c.match(/[\\d.]+/g).map(Number); return {r: m[0], g: m[1], b: m[2], a: m.length > 3 ? m[3] : 1}; };
  const over = (top, under) => ({r: top.r * top.a + under.r * (1 - top.a), g: top.g * top.a + under.g * (1 - top.a), b: top.b * top.a + under.b * (1 - top.a), a: 1});
  const channel = v => { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
  const luminance = c => 0.2126 * channel(c.r) + 0.7152 * channel(c.g) + 0.0722 * channel(c.b);
  const backdrop = node => {
    const layers = [];
    for (let e = node; e; e = e.parentElement) { layers.push(parse(getComputedStyle(e).backgroundColor)); }
    return layers.reverse().reduce((under, top) => over(top, under), {r: 255, g: 255, b: 255, a: 1});
  };
  const root = document.querySelector(s.page);
  const worst = [...root.querySelectorAll(s.readable)].filter(e => e.offsetParent !== null && e.textContent.trim() !== '').map(e => {
    const under = backdrop(e);
    const text = over(parse(getComputedStyle(e).color), under);
    const a = luminance(text), b = luminance(under);
    return {ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05), text: e.textContent.trim().slice(0, 40)};
  }).sort((x, y) => x.ratio - y.ratio)[0];
  return {worst, background: luminance(backdrop(root))};
}"""


def themes(s, width):
    """The page follows the theme chosen in Jellyfin and stays readable."""
    for theme, dark in [("dark", True), ("light", False)]:
        name = f"{width} {theme} theme"
        page = s.open("fresh", width, theme=theme)
        check(name + " is loaded", u.theme_of(page) == theme, u.theme_of(page))
        seen = page.evaluate(CONTRAST, {"page": u.PAGE, "readable": u.READABLE})
        check(name + " sets the background", (seen["background"] < 0.2) == dark, seen["background"])
        check(name + " text is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
        healthy(page, name)
        u.shot(page, f"theme-{theme}-{width}")
        page.context.close()
    u.set_theme("fresh", "dark")


def actions(s, width):
    """The actions that work: each one reaches the server."""
    name = width + " actions"
    page = s.open("fresh", width)

    stock = u.server_row(page, "stock")
    u.button(stock, "test").click()
    shown = u.wait_message(page)
    check(name + " a connection test reports", "connected" in shown, shown)

    u.clear_message(page)
    stock = u.server_row(page, "stock")
    u.button(stock, "libraries").click()
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    check(name + " the libraries show what is imported", stock.locator(u.CHECKBOX + ":checked").count() == 2)
    u.button(stock, "save_libraries").click()
    check(name + " the libraries save", "saved" in u.wait_message(page))

    u.clear_message(page)
    connected = u.peer_row(page, "jellymesh")
    connected.locator(u.NUMBER).fill("3")
    u.button(connected, "save").click()
    u.wait_message(page)
    limit = u.api("fresh", "GET", "/JellyMesh/Admin/Peers")[1][0]["MaxStreams"]
    check(name + " a connected server saves", limit == 3, limit)
    connected = u.peer_row(page, "jellymesh")
    connected.locator(u.NUMBER).fill("4")
    u.button(connected, "save").click()
    until(page, lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Peers")[1][0]["MaxStreams"] == 4)

    u.clear_message(page)
    case = u.merge_case(page, PAIR)
    for box in case.locator(u.CHECKBOX).all():
        box.check()
    u.button(case, "merge").click()
    page.wait_for_selector(u.MERGE, state="attached", timeout=30000)
    check(name + " a merge is listed", PAIR in page.locator(u.MERGE).inner_text())
    check(name + " a merge reports", "Merged" in u.wait_message(page), u.message(page))
    u.shot(page, f"merged-{width}")
    u.clear_message(page)
    u.button(page.locator(u.MERGE), "undo").click()
    u.answer_confirm(page, True)
    page.wait_for_selector(u.MERGE_CASE + f":has-text('{PAIR}')", state="attached", timeout=30000)
    check(name + " an undo brings the case back", u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]["Merges"] == [])

    u.clear_message(page)
    page.locator(u.SYNC_INTERVAL).fill("901")
    page.locator(u.SAVE_SETTINGS).click()
    check(name + " the settings save", until(page, lambda: u.config("fresh")["SyncIntervalSeconds"] == 901))
    check(name + " nothing else changed", page.locator(u.FEDERATED_FOLDER).input_value() == u.config("fresh")["FederatedFolderPath"])
    u.settings("fresh", lambda config: config.update(SyncIntervalSeconds=900))
    healthy(page, name)
    page.context.close()


def offline(s, width):
    """The stock remote stopped, and a sync found out."""
    name = width + " offline remote"
    page = s.open("fresh", width)
    stock = u.server_row(page, "stock")
    check(name + " marks the remote", "offline" in stock.inner_text(), stock.inner_text())
    check(name + " leaves the other online", "online" in u.server_row(page, "peer").inner_text())
    lines = text(page, u.SYNC_SERVERS)
    check(name + " says the catalog is kept", re.search(r"stock: [1-9]\d* films.*offline", lines, re.I | re.S) is not None, lines)
    u.shot(page, f"offline-{width}")
    u.button(stock, "test").click()
    shown = u.wait_message(page)
    check(name + " a connection test says so", "cannot be reached" in shown, shown)
    healthy(page, name)
    page.context.close()


def nokey(s, width):
    """Servers saved, and the secret key gone."""
    name = width + " no key"
    page = s.open("fresh", width)
    warning = text(page, u.WARNING) if page.locator(u.WARNING).is_visible() else ""
    check(name + " warns", "JELLYMESH_SECRET_KEY" in warning and "suspended" in warning, warning)
    check(name + " is suspended", "suspended" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " cannot add a server", page.locator(u.ADD_SUBMIT).is_disabled())
    check(name + " cannot sync", page.locator(u.SYNC_NOW).is_disabled())
    check(name + " still lists the servers", page.locator(u.SERVER).count() == 2)
    check(name + " shows the settings", page.locator(u.SYNC_INTERVAL).input_value() != "")
    seen = page.evaluate(CONTRAST, {"page": u.PAGE, "readable": u.WARNING})
    check(name + " the warning is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
    healthy(page, name)
    u.shot(page, f"nokey-{width}")
    page.context.close()


def edits(s, width):
    """An action leaves alone what it did not change: open panels, unsaved edits and keyboard focus."""
    name = width + " edits"
    page = s.open("fresh", width)
    stock = u.server_row(page, "stock")
    connected = u.peer_row(page, "jellymesh")
    new_peer = u.peer_row(page, u.NEW_PEER)
    u.button(stock, "libraries").click()
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    stock.locator(u.CHECKBOX).first.uncheck()
    connected.locator(u.NUMBER).fill("9")
    connected.locator(u.CHECKBOX).first.uncheck()

    def kept(action, limit="9"):
        check(f"{name} {action} keeps the open panel", stock.locator(u.CHECKBOX).count() == 2, stock.locator(u.CHECKBOX).count())
        check(f"{name} {action} keeps the unchecked library", stock.locator(u.CHECKBOX + ":checked").count() == 1)
        check(f"{name} {action} keeps the unsaved limit", connected.locator(u.NUMBER).input_value() == limit, connected.locator(u.NUMBER).input_value())
        check(f"{name} {action} keeps the unchecked peer box", connected.locator(u.CHECKBOX + ":checked").count() == 1)

    test = u.button(stock, "test")
    test.focus()
    page.keyboard.press("Enter")
    shown = u.wait_message(page)
    check(name + " a connection test reports", "connected" in shown, shown)
    kept("a connection test")
    check(name + " a connection test keeps the focus", u.has_focus(test))

    u.clear_message(page)
    page.locator(u.SYNC_INTERVAL).fill("902")
    page.locator(u.SAVE_SETTINGS).click()
    check(name + " the settings save", until(page, lambda: u.config("fresh")["SyncIntervalSeconds"] == 902))
    u.wait_idle(page)
    kept("a saved setting")
    u.settings("fresh", lambda config: config.update(SyncIntervalSeconds=900))

    held = []
    page.route("**/JellyMesh/Admin/Status", lambda route: held.append(route))
    u.clear_message(page)
    u.button(stock, "test").click()
    until(page, lambda: u.message(page) != "")
    connected.locator(u.NUMBER).fill("7")
    until(page, lambda: held)
    for route in held:
        route.continue_()
    page.unroute("**/JellyMesh/Admin/Status")
    u.wait_idle(page)
    kept("a value typed during a refresh", "7")

    u.clear_message(page)
    u.button(stock, "save_libraries").click()
    u.wait_message(page)
    kept("saved libraries", "7")
    check(name + " saved libraries update their row", "libraries to import: 1" in stock.inner_text(), stock.inner_text())
    stock.locator(u.CHECKBOX).first.check()
    u.clear_message(page)
    u.button(stock, "save_libraries").click()
    u.wait_message(page)

    new_peer.locator(u.NUMBER).fill("5")
    new_peer.locator(u.CHECKBOX).first.check()
    connected.locator(u.NUMBER).fill("4")
    save = u.button(connected, "save")
    u.clear_message(page)
    save.focus()
    page.keyboard.press("Enter")
    u.wait_message(page)
    check(name + " a saved peer keeps the other row's limit", new_peer.locator(u.NUMBER).input_value() == "5", new_peer.locator(u.NUMBER).input_value())
    check(name + " a saved peer keeps the other row's box", new_peer.locator(u.CHECKBOX + ":checked").count() == 1)
    check(name + " a saved peer keeps the focus", u.has_focus(save))
    check(name + " a saved peer shows what was saved", connected.locator(u.CHECKBOX + ":checked").count() == 1)
    connected.locator(u.CHECKBOX).first.check()
    u.clear_message(page)
    u.button(connected, "save").click()
    u.wait_message(page)

    other = u.merge_case(page, CHAIN).locator(u.CHECKBOX).first
    other.check()
    case = u.merge_case(page, PAIR)
    for box in case.locator(u.CHECKBOX).all():
        box.check()
    merge = u.button(case, "merge")
    u.clear_message(page)
    merge.focus()
    page.keyboard.press("Enter")
    u.wait_message(page)
    check(name + " a merge keeps the selection in another case", other.is_checked())
    check(name + " a merge leaves the focus in the list", u.has_focus(page.locator(u.MERGE_CASES)))
    u.clear_message(page)
    u.button(page.locator(u.MERGE), "undo").click()
    u.answer_confirm(page, True)
    u.wait_message(page)
    check(name + " an undo keeps the selection in another case", other.is_checked())
    healthy(page, name)
    page.context.close()


def fake(page, path, change):
    """Rewrites the reads of an admin path with change, which sees the decoded answer."""
    def answer(route):
        if route.request.method != "GET":
            route.continue_()
            return
        response = route.fetch()
        data = response.json()
        change(data)
        route.fulfill(response=response, json=data)

    page.route("**/JellyMesh/Admin/" + path, answer)


def quiet(s, width):
    """A page left open stays cheap, and a hidden tab reads nothing."""
    name = width + " quiet"
    page = s.open("fresh", width)
    reads = []
    page.on("request", lambda request: reads.append(request.url.split("/JellyMesh/Admin/")[1]) if "/JellyMesh/Admin/" in request.url else None)
    page.wait_for_timeout(31000)
    counts = {path: reads.count(path) for path in ["Sync", "Status", "Peers", "Peers/Options", "Merges"]}
    check(name + " reads the sync every 5 seconds", 5 <= counts["Sync"] <= 7, counts)
    check(name + " reads status and streams every 15 seconds", 1 <= counts["Status"] <= 3 and 1 <= counts["Peers"] <= 3, counts)
    check(name + " reads the peer options at most once a minute", counts["Peers/Options"] <= 1, counts)
    check(name + " leaves the merges alone", counts["Merges"] == 0, counts)
    check(name + " reads nothing else", len(reads) == sum(counts.values()), reads)

    page.evaluate("""() => {
      Object.defineProperty(document, 'hidden', {value: true, configurable: true});
      document.dispatchEvent(new Event('visibilitychange'));
    }""")
    page.wait_for_timeout(500)
    reads.clear()
    page.wait_for_timeout(7000)
    check(name + " a hidden tab reads nothing", not reads, reads)
    page.evaluate("""() => {
      delete document.hidden;
      document.dispatchEvent(new Event('visibilitychange'));
    }""")
    check(name + " a shown tab catches up at once", until(page, lambda: "Status" in reads and "Peers" in reads and "Sync" in reads, 2), reads)
    healthy(page, name)
    page.context.close()


def polling(s, width):
    """The page follows what happens elsewhere, without touching what the admin is editing."""
    name = width + " polling"
    page = s.open("fresh", width)
    status_line = page.locator(u.SYNC_STATUS)
    sync_now = page.locator(u.SYNC_NOW)
    stock = u.server_row(page, "stock")
    connected = u.peer_row(page, "jellymesh")
    limit = connected.locator(u.NUMBER)
    u.mark(stock)
    u.mark(connected)
    limit.focus()
    limit.fill("6")

    sync = {"running": False, "fail": None, "stall": False}
    held = []
    reads = []

    def sync_route(route):
        if route.request.method != "GET":
            route.continue_()
        elif sync["stall"]:
            held.append(route)
        elif sync["fail"] == "error":
            route.fulfill(status=500, json={})
        elif sync["fail"] == "network":
            route.abort()
        else:
            response = route.fetch()
            data = response.json()
            data["IsRunning"] = data["IsRunning"] or sync["running"]
            route.fulfill(response=response, json=data)

    page.route("**/JellyMesh/Admin/Sync", sync_route)
    page.on("request", lambda request: reads.append(request.url) if request.url.endswith("/JellyMesh/Admin/Sync") else None)

    sync["running"] = True
    check(name + " a sync started elsewhere appears", until(page, lambda: "running" in status_line.inner_text().lower(), 8), status_line.inner_text())
    check(name + " the button waits for it", sync_now.is_disabled())
    sync["running"] = False
    check(name + " the end of that sync appears", until(page, lambda: "Last sync" in status_line.inner_text(), 8), status_line.inner_text())
    check(name + " the button comes back", sync_now.is_enabled())

    before = status_line.inner_text()
    page.wait_for_timeout(1100)
    started = u.api("fresh", "POST", "/JellyMesh/Admin/Sync")[0]
    check(name + " a real sync starts", started == 202, started)
    check(name + " a real sync updates the last sync", until(page, lambda: "Last sync" in status_line.inner_text() and status_line.inner_text() != before, 30), status_line.inner_text())

    fake(page, "Peers", lambda peers: peers[0].update(ActiveStreams=1))
    fake(page, "Status", lambda status: [server.update(Connection="Offline") for server in status["Servers"] if server["Name"] == "stock"])
    check(name + " stream use stays current", until(page, lambda: "Streams in use: 1 of" in connected.inner_text(), 18), connected.inner_text())
    check(name + " server state stays current", until(page, lambda: "offline" in stock.inner_text(), 18), stock.inner_text())
    check(name + " the rows were not rebuilt", u.is_marked(stock) and u.is_marked(connected))
    check(name + " the row being edited keeps its value", limit.input_value() == "6", limit.input_value())
    check(name + " the row being edited keeps the focus", u.has_focus(limit))

    for failure, label in [("error", "a server error"), ("network", "a network failure")]:
        sync["running"] = True
        sync_now.click()
        until(page, lambda: "running" in status_line.inner_text().lower(), 8)
        sync["fail"] = failure
        note = page.locator(u.FAILURE["sync"])
        check(f"{name} {label} shows in place", until(page, note.is_visible, 8))
        check(f"{name} {label} does not claim a running sync", "running" not in status_line.inner_text().lower(), status_line.inner_text())
        check(f"{name} {label} frees the button", sync_now.is_enabled())
        sync["running"] = False
        sync["fail"] = None
        check(f"{name} polling recovers from {label}", until(page, lambda: not note.is_visible() and "Last sync" in status_line.inner_text(), 8), status_line.inner_text())

    sync["stall"] = True
    check(name + " a stalled read arrives", until(page, lambda: held, 8))
    page.wait_for_timeout(7000)
    check(name + " no read starts on top of a stalled one", len(held) == 1, len(held))
    note = page.locator(u.FAILURE["sync"])
    check(name + " a stalled read shows in place", until(page, note.is_visible, 12))
    sync["stall"] = False
    check(name + " polling recovers from a stall", until(page, lambda: not note.is_visible(), 10), status_line.inner_text())

    page.evaluate("location.hash = '#/dashboard'")
    page.wait_for_function("() => !location.hash.includes('configurationpage')", timeout=15000)
    page.wait_for_timeout(1000)
    reads.clear()
    page.wait_for_timeout(7000)
    check(name + " polling stops when the page is left", not reads, reads)
    page.go_back()
    u.wait_loaded(page)
    reads.clear()
    check(name + " polling resumes when the page is back", until(page, lambda: reads, 8))
    healthy(page, name)
    page.unroute_all(behavior="ignoreErrors")
    page.context.close()


def sections(s, width):
    """Each part of the page loads on its own, and the one that fails says so in its place."""
    parts = {
        "status": "**/JellyMesh/Admin/Status",
        "sync": "**/JellyMesh/Admin/Sync",
        "peers": "**/JellyMesh/Admin/Peers",
        "merges": "**/JellyMesh/Admin/Merges",
        "settings": u.PLUGIN_CONFIG,
    }

    def filled(page):
        """Tells which parts have their data."""
        return {
            "status": page.locator(u.SERVER).count() == 2,
            "sync": "Last sync" in text(page, u.SYNC_STATUS),
            "peers": u.peer_row(page, "jellymesh").count() == 1,
            "merges": u.merge_case(page, PAIR).count() == 1 and text(page, u.MERGES) != "",
            "settings": page.locator(u.SYNC_INTERVAL).input_value() != "",
        }

    for part, pattern in parts.items():
        name = f"{width} {part} failing"
        state = {"fail": True}

        def broken(context, pattern=pattern, state=state):
            context.route(pattern, lambda route: route.fulfill(status=500, json={}) if state["fail"] else route.continue_())

        page = s.open("fresh", width, route=broken, wait=False)
        page.wait_for_selector(u.PAGE, state="attached", timeout=30000)
        note = page.locator(u.FAILURE[part])
        check(name + " shows in place", until(page, note.is_visible, 20), part)
        u.wait_idle(page)
        ready = filled(page)
        for other in parts:
            if other != part:
                check(f"{name} leaves {other} loaded", ready[other], ready)
                check(f"{name} leaves {other} without a failure", not page.locator(u.FAILURE[other]).is_visible())
        check(name + " is not filled", not ready[part], ready)
        check(name + " can still sync", page.locator(u.SYNC_NOW).is_enabled())
        u.shot(page, f"section-{part}-{width}")
        if part != "settings":
            state["fail"] = False
            check(name + " recovers", until(page, lambda: not note.is_visible() and filled(page)[part], 12), filled(page))
        healthy(page, name)
        page.context.close()


SETTING_ERRORS = [
    (u.SYNC_INTERVAL, "60", "Sync interval", "at least 300"),
    (u.SYNC_INTERVAL, "", "Sync interval", "at least 300"),
    ("#jmRemotePlaybackInfoTimeout", "2", "Wait for a remote", "from 3 to 60"),
    ("#jmStreamOpenTimeout", "61", "Wait for each step", "from 5 to 60"),
    ("#jmPlaybackIdle", "110", "Close an idle remote", "from 30 to 109"),
    ("#jmSessionRetention", "0", "Keep paused sessions", "from 1 to 168"),
    ("#jmPolicyCache", "1.5", "Remember the transcoding rights", "from 1 to 60"),
    ("#jmReconciliationTime", "", "Nightly reconciliation time", "hours and minutes"),
    (u.FEDERATED_FOLDER, "relative/path", "Federated folder", "absolute path"),
    (u.FEDERATED_FOLDER, "/", "Federated folder", "absolute path"),
    (u.FEDERATED_FOLDER, "/films/../other", "Federated folder", "absolute path"),
]


def outcome(page, name, kind, says=""):
    """Checks that an outcome has its word, stands in view, and is what the status region holds."""
    shown = u.wait_message(page)
    check(f"{name} says {kind}", shown.startswith(kind + ": "), shown)
    check(name + " says what happened", says in shown, shown)
    check(name + " is in view", u.in_view(page, u.OUTCOME), shown)
    region = page.get_by_role("status").filter(has_text=shown)
    check(name + " is in the status region", region.count() == 1 and region.get_attribute("id") == u.MESSAGE[1:])
    return shown


def outcomes_shown(page, name):
    """Outcomes of the actions, where they appear, and when they go."""
    check(name + " the region is rendered before it has text", page.locator(u.MESSAGE).evaluate(
        "node => node.getAttribute('role') === 'status' && getComputedStyle(node).display !== 'none' && !node.closest('[hidden]')"))
    check(name + " shows no outcome yet", u.message(page) == "" and not page.locator(u.DISMISS).is_visible())

    case = u.merge_case(page, PAIR)
    case.locator(u.CHECKBOX).first.check()
    u.button(case, "merge").click()
    outcome(page, name + " a refused merge", "Error", "at least two")
    error = page.locator(u.OUTCOME).evaluate("node => getComputedStyle(node).backgroundColor")
    u.button(u.server_row(page, "stock"), "libraries").click()
    u.wait_idle(page)
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    check(name + " opening the libraries clears the old error", u.message(page) == "", u.message(page))

    u.button(u.server_row(page, "stock"), "test").click()
    outcome(page, name + " a connection test", "Done", "connected")
    done = page.locator(u.OUTCOME).evaluate("node => getComputedStyle(node).backgroundColor")
    check(name + " an error and a success differ in colour", error != done, done)

    hold = []
    page.route("**/JellyMesh/Admin/Servers/*", lambda route: hold.append(route) if route.request.method == "DELETE" else route.continue_())
    u.button(u.server_row(page, "stock"), "remove").click()
    page.locator(u.CONFIRM_OK).click()
    until(page, lambda: hold)
    check(name + " a new action clears the old outcome at once", u.message(page) == "", u.message(page))
    hold.pop().fulfill(status=204)
    outcome(page, name + " a removal", "Done", "Removed stock")

    page.route("**/JellyMesh/Admin/Merges", lambda route: route.fulfill(status=200, json={}) if route.request.method == "POST" else route.continue_())
    case = u.merge_case(page, PAIR)
    for box in case.locator(u.CHECKBOX).all():
        box.check()
    u.button(case, "merge").click()
    outcome(page, name + " a merge", "Done", "Merged")
    page.unroute("**/JellyMesh/Admin/Merges")

    page.locator(u.DISMISS).click()
    check(name + " Dismiss empties the outcome", u.message(page) == "" and not page.locator(u.DISMISS).is_visible(), u.message(page))


def settings_refused(page, name):
    """Each wrong setting is named with its rule and focused, and nothing reaches the server."""
    saves = []
    page.on("request", lambda request: saves.append(request.url) if "/Plugins/" in request.url and request.method == "POST" else None)
    for selector, value, field, rule in SETTING_ERRORS:
        label = f"{name} {selector} set to '{value}'"
        before = page.locator(selector).input_value()
        page.locator(selector).fill(value)
        page.locator(u.SAVE_SETTINGS).click()
        shown = outcome(page, label, "Error", field)
        check(label + " gives the rule", rule in shown, shown)
        check(label + " says nothing was saved", "nothing was saved" in shown, shown)
        check(label + " marks the field", rule in u.field_error(page, selector), u.field_error(page, selector))
        check(label + " is focused", "#" + u.focused(page) == selector, u.focused(page))
        check(label + " the field is in view", u.in_view(page, selector))
        healthy(page, label)
        page.locator(selector).fill(before)
    check(name + " sends none of them to the server", saves == [], saves)

    policy = page.locator("#jmPolicyCache").input_value()
    opening = page.locator("#jmStreamOpenTimeout").input_value()
    page.locator("#jmPolicyCache").fill("0")
    page.locator("#jmStreamOpenTimeout").fill("0")
    page.locator(u.SAVE_SETTINGS).click()
    shown = outcome(page, name + " two wrong fields", "Error", "Wait for each step")
    check(name + " two wrong fields counts the other", "1 more field" in shown, shown)
    check(name + " two wrong fields focuses the first", u.focused(page) == "jmStreamOpenTimeout", u.focused(page))
    check(name + " two wrong fields marks both", u.field_error(page, "#jmPolicyCache") != "" and u.field_error(page, "#jmStreamOpenTimeout") != "")
    u.shot(page, f"outcome-fields-{page.viewport_size['width']}")
    page.locator("#jmPolicyCache").fill(policy)
    page.locator("#jmStreamOpenTimeout").fill(opening)


def settings_failed(page, name):
    """Failures the page cannot foresee: the server refuses, breaks, or does not answer."""
    config = u.PLUGIN_CONFIG

    folder = page.locator(u.FEDERATED_FOLDER).input_value()
    page.locator(u.FEDERATED_FOLDER).fill("C:\\films")
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a save the server refuses", "Error", "server refused")
    check(name + " the field errors have gone", page.locator(u.FIELD_ERROR).count() == 0)
    page.locator(u.FEDERATED_FOLDER).fill(folder)

    def post(answer):
        page.route(config, lambda route: answer(route) if route.request.method == "POST" else route.continue_())

    post(lambda route: route.fulfill(status=500, body=""))
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a server error", "Error", "error 500")
    page.unroute(config)

    post(lambda route: route.abort())
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a save with no answer", "Error", "No answer from this server")
    page.unroute(config)

    page.route("**/JellyMesh/Admin/Servers/*/Test", lambda route: route.abort())
    u.button(u.server_row(page, "stock"), "test").click()
    outcome(page, name + " an action with no answer", "Error", "No answer from this server")
    page.unroute("**/JellyMesh/Admin/Servers/*/Test")

    page.context.set_offline(True)
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a save offline", "Error", "No answer from this server")
    page.context.set_offline(False)


def peer_limit(page, name):
    """An empty stream limit is named and focused, and never sent."""
    sent = []
    page.on("request", lambda request: sent.append(request.url) if request.method == "PUT" else None)
    connected = u.peer_row(page, "jellymesh")
    limit = connected.locator(u.NUMBER)
    limit.fill("")
    u.button(connected, "save").click()
    shown = outcome(page, name + " an empty stream limit", "Error", "Stream limit")
    check(name + " an empty stream limit gives the rule", "at least 1" in shown, shown)
    check(name + " an empty stream limit is marked", "at least 1" in connected.locator(u.FIELD_ERROR).inner_text())
    check(name + " an empty stream limit is focused", limit.evaluate("node => node === document.activeElement"))
    check(name + " an empty stream limit is not sent", sent == [], sent)
    u.shot(page, f"outcome-peer-{page.viewport_size['width']}")
    limit.fill("4")
    u.button(connected, "save").click()
    outcome(page, name + " a corrected stream limit", "Done", "Saved jellymesh")
    check(name + " a corrected stream limit loses its mark", page.locator(u.FIELD_ERROR).count() == 0)


def outcomes(s, width):
    """What an action leaves on the page, at the width of a phone too."""
    name = width + " outcomes"
    before = u.config("fresh")
    page = s.open("fresh", width)
    outcomes_shown(page, name)
    healthy(page, name)
    page.context.close()

    page = s.open("fresh", width)
    settings_refused(page, name)
    settings_failed(page, name)
    peer_limit(page, name)
    healthy(page, name)
    check(name + " left the settings as they were", u.config("fresh") == before)
    page.context.close()

    name = width + " outcome of a failed load"

    def broken(context):
        context.route("**/JellyMesh/Admin/Status", lambda route: route.fulfill(status=500, body=""))

    page = s.open("fresh", width, route=broken, wait=False)
    outcome(page, name, "Error", "error 500")
    page.context.close()


def outcome_themes(s, width):
    """Both kinds of outcome and a marked field stay readable in the dark and light themes."""
    for theme in ["dark", "light"]:
        name = f"{width} {theme} outcomes"
        page = s.open("fresh", width, theme=theme)
        policy = page.locator("#jmPolicyCache").input_value()
        page.locator("#jmPolicyCache").fill("0")
        page.locator(u.SAVE_SETTINGS).click()
        u.wait_message(page)
        seen = page.evaluate(CONTRAST, {"page": u.PAGE, "readable": u.MESSAGE + ", " + u.DISMISS + ", " + u.FIELD_ERROR})
        check(name + " an error is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
        page.screenshot(path=f"{u.SHOTS}/outcome-error-{theme}-{width}.png")
        page.locator("#jmPolicyCache").fill(policy)

        u.button(u.server_row(page, "stock"), "test").click()
        outcome(page, name + " a success", "Done", "connected")
        seen = page.evaluate(CONTRAST, {"page": u.PAGE, "readable": u.MESSAGE + ", " + u.DISMISS})
        check(name + " a success is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
        page.screenshot(path=f"{u.SHOTS}/outcome-done-{theme}-{width}.png")
        page.context.close()
    u.set_theme("fresh", "dark")


def confirm_shows(page, name, title, says, viewport):
    """Checks what a confirmation dialog says, that Cancel is the safe default, and that it fits the screen."""
    shown_title, shown_text = u.wait_confirm(page)
    check(name + " has the title", shown_title == title, shown_title)
    for words in says:
        check(f"{name} says: {words}", words in shown_text, shown_text)
    check(name + " renders no markup from a name", page.locator(u.CONFIRM_MARKUP).count() == 0)
    check(name + " starts on Cancel", u.confirm_starts_on_cancel(page))
    box = page.locator(u.CONFIRM).bounding_box()
    check(name + " fits the screen", box["x"] >= 0 and box["x"] + box["width"] <= viewport["width"]
          and box["y"] >= 0 and box["y"] + box["height"] <= viewport["height"], box)


def confirm_keeps(page, name, stays):
    """Cancels a confirmation dialog and checks that nothing happened."""
    u.answer_confirm(page, False)
    check(name + " Cancel changes nothing", stays())


def hostile_peer(context):
    """Gives the connected servers a name that would be markup if it were not escaped."""
    def rename(route):
        answer = route.fetch()
        route.fulfill(response=answer, json=[dict(peer, UserName=HOSTILE_PEER) for peer in answer.json()])

    context.route("**/JellyMesh/Admin/Peers", rename)


def confirms(s, width):
    """The destructive actions ask first, say what happens, and do it only when confirmed."""
    name = width + " confirm"
    viewport = u.VIEWPORTS[width]
    status = lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"]
    peers = lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Peers")[1]
    merges = lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]
    stock = next(server for server in status() if server["Name"] == "stock")
    added = u.api("fresh", "POST", "/JellyMesh/Admin/Servers", {
        "Name": HOSTILE, "Url": stock["Url"], "Username": "jellymesh", "Password": "jellymesh-test"})
    check(name + " has a remote to remove", added[0] == 200, added)
    friend = next(user for user in u.api("fresh", "GET", "/Users")[1] if user["Name"] == "friend")
    library = u.api("fresh", "GET", "/JellyMesh/Admin/Peers/Options")[1]["Libraries"][0]["Id"]
    u.api("fresh", "PUT", "/JellyMesh/Admin/Peers/" + friend["Id"], {"SharedLibraryIds": [library], "MaxStreams": 1})
    case = next(group for group in merges()["Cases"] if PAIR in str(group))
    u.api("fresh", "POST", "/JellyMesh/Admin/Merges", {"WorkKeys": [title["WorkKey"] for title in case["Titles"]]})
    page = s.open("fresh", width)
    count = len(status())

    remove = u.button(u.server_row(page, HOSTILE), "remove")
    remove.click()
    confirm_shows(page, name + " remove", "Remove this server?", [
        HOSTILE + " and its stored login leave JellyMesh now", "login is not revoked", "do not play", "next sync",
        "does not start a sync", "without this backup", "Doubtful films", "may not stop right away", "same titles and watch state"], viewport)
    u.shot(page, f"confirm-remove-{width}", full_page=False)
    confirm_keeps(page, name + " remove", lambda: len(status()) == count and u.server_row(page, HOSTILE).count() == 1)
    remove.click()
    u.answer_confirm(page, True)
    check(name + " remove acts", until(page, lambda: len(status()) == count - 1 and u.server_row(page, HOSTILE).count() == 0))
    check(name + " remove leaves the others", u.server_row(page, "stock").count() == 1 and u.server_row(page, "peer").count() == 1)

    row = u.peer_row(page, "friend")
    check(name + " one word for a connected server", u.button(row, "disconnect").count() == 1 and u.button(row, "remove").count() == 0)
    u.button(row, "disconnect").click()
    confirm_shows(page, name + " disconnect", "Disconnect this server?", [
        "friend stops being a connected server now", "streams here are closed", "can still log in", "standard mode", "no stream limit",
        "Dashboard > Users"], viewport)
    u.shot(page, f"confirm-disconnect-{width}", full_page=False)
    held = len(peers())
    confirm_keeps(page, name + " disconnect", lambda: len(peers()) == held)
    u.button(u.peer_row(page, "friend"), "disconnect").click()
    u.answer_confirm(page, True)
    check(name + " disconnect acts", until(page, lambda: len(peers()) == held - 1 and u.peer_row(page, "friend").count() == 0))
    check(name + " disconnect keeps the user", any(user["Name"] == "friend" for user in u.api("fresh", "GET", "/Users")[1]))

    u.button(page.locator(u.MERGE), "undo").click()
    confirm_shows(page, name + " undo", "Undo this merge?", ["separate titles again", "keeps its watch state", "Local files are not changed"], viewport)
    u.shot(page, f"confirm-undo-{width}", full_page=False)
    confirm_keeps(page, name + " undo", lambda: len(merges()["Merges"]) == 1)
    u.button(page.locator(u.MERGE), "undo").click()
    u.answer_confirm(page, True)
    check(name + " undo acts", until(page, lambda: merges()["Merges"] == [] and page.locator(u.MERGE).count() == 0))
    healthy(page, name)
    page.context.close()

    page = s.open("fresh", width, route=hostile_peer)
    u.button(u.peer_row(page, HOSTILE_PEER), "disconnect").click()
    shown = u.wait_confirm(page)[1]
    check(name + " a user name shows as text", HOSTILE_PEER + " stops being" in shown, shown)
    check(name + " a user name renders no markup", page.locator(u.CONFIRM_MARKUP).count() == 0)
    confirm_keeps(page, name + " a hostile name", lambda: len(peers()) == held - 1)
    healthy(page, name)
    page.context.close()


def confirm_keyboard(s, width):
    """A confirmation can be opened and refused from the keyboard alone."""
    name = width + " confirm keyboard"
    page = s.open("fresh", width)
    remove = u.button(u.server_row(page, "stock"), "remove")
    remove.focus()
    page.keyboard.press("Enter")
    u.wait_confirm(page)
    page.keyboard.press("Enter")
    page.wait_for_selector(u.CONFIRM, state="detached", timeout=15000)
    servers = u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"]
    check(name + " Enter on the first focus cancels", u.server_row(page, "stock").count() == 1 and len(servers) == 2, servers)
    healthy(page, name)
    page.context.close()


SCENARIOS = {
    "first": (first, WIDTHS),
    "first-nokey": (first_nokey, WIDTHS),
    "normal": (normal, WIDTHS),
    "loading": (loading, WIDTHS),
    "errors": (errors, WIDTHS),
    "keyboard": (keyboard, WIDTHS),
    "themes": (themes, WIDTHS),
    "actions": (actions, ["1280"]),
    "confirms": (confirms, WIDTHS),
    "confirm-keyboard": (confirm_keyboard, WIDTHS),
    "offline": (offline, WIDTHS),
    "nokey": (nokey, WIDTHS),
    "edits": (edits, ["1280"]),
    "quiet": (quiet, ["1280"]),
    "polling": (polling, ["1280"]),
    "sections": (sections, ["1280"]),
    "outcomes": (outcomes, WIDTHS),
    "outcome-themes": (outcome_themes, WIDTHS),
}

with u.Session() as session:
    for scenario in sys.argv[1:]:
        run, widths = SCENARIOS[scenario]
        for size in widths:
            try:
                run(session, size)
            except Exception as error:
                check(f"{size} {scenario} ran to the end", False, str(error).splitlines()[0])

sys.exit(FAILED[0])
