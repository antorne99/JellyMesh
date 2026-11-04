#!/usr/bin/env python3
"""Scans every byte the browser received, plus stored API answers, for values of the remotes.
Secrets come from the gate logs (what really went to the remotes) and from the stub registry."""
import glob, json, re, sys
G = sys.argv[1] + "/run"
tokens, plays, devices, items = set(), set(), set(), set()
for f in glob.glob(G + "/gatelog-*.all"):
    for line in open(f, errors="ignore"):
        m = re.search(r"auth=(\w{16,})", line)
        if m:
            tokens.add(m[1])
        for rx, bag in ((r"PlaySessionId=(\w+)", plays), (r"DeviceId=([\w-]+)", devices)):
            m = re.search(rx, line)
            if m:
                bag.add(m[1])
        m = re.search(r"/(?:videos|Videos|Items)/([0-9a-f-]{32,36})/", line)
        if m:
            items.add(m[1]); items.add(m[1].replace("-", ""))
stubs = json.load(open(G + "/stubs3.json"))
servers = set()
for s in stubs["Stubs"]:
    for src in s["Sources"]:
        if src["ItemId"] != "acbe03b163b13bf1cc9cc9b9dd068460":
            items.add(src["ItemId"])
        servers.add(src["ServerId"]); servers.add(src["ServerId"].replace("-", ""))
fixed = ["18321", "18322", ":8097", ":8098", "remoto-standard", "remoto-jellymesh", "/media/", "tailnet", "remote-error-body-sentinel"]
print("secrets:", {k: len(v) for k, v in (("tokens", tokens), ("plays", plays), ("devices", devices), ("items", items), ("servers", servers))})
assert tokens and plays and devices and items
targets = list(glob.glob(G + "/cap-*.bin")) + list(glob.glob(G + "/cap-*.txt")) + list(glob.glob(G + "/api-*.json"))
total, hits = 0, 0
for f in targets:
    b = open(f, "rb").read()
    total += len(b)
    for label, bag in (("token", tokens), ("playsession", plays), ("device", devices), ("item", items), ("server", servers), ("fixed", fixed)):
        for v in bag:
            if v.encode() in b:
                i = b.index(v.encode())
                print("HIT", label, v[:24], f.split("/")[-1], b[max(0, i - 70):i + 40])
                hits += 1
print(f"scanned {len(targets)} files, {total} bytes, hits {hits}")
