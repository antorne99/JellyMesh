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
    save.focus()
    u.clear_message(page)
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
    merge.focus()
    u.clear_message(page)
    page.keyboard.press("Enter")
    u.wait_message(page)
    check(name + " a merge keeps the selection in another case", other.is_checked())
    check(name + " a merge leaves the focus in the list", u.has_focus(page.locator(u.MERGE_CASES)))
    u.clear_message(page)
    u.button(page.locator(u.MERGE), "undo").click()
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
    check(name + " stream use stays current", until(page, lambda: "Streams in use: 1 of" in connected.inner_text(), 8), connected.inner_text())
    check(name + " server state stays current", until(page, lambda: "offline" in stock.inner_text(), 8), stock.inner_text())
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
        "settings": "**/Plugins/%s/Configuration" % u.PLUGIN,
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


SCENARIOS = {
    "first": (first, WIDTHS),
    "first-nokey": (first_nokey, WIDTHS),
    "normal": (normal, WIDTHS),
    "loading": (loading, WIDTHS),
    "errors": (errors, WIDTHS),
    "keyboard": (keyboard, WIDTHS),
    "themes": (themes, WIDTHS),
    "actions": (actions, ["1280"]),
    "offline": (offline, WIDTHS),
    "nokey": (nokey, WIDTHS),
    "edits": (edits, ["1280"]),
    "polling": (polling, ["1280"]),
    "sections": (sections, ["1280"]),
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
