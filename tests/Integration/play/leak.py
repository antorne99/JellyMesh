#!/usr/bin/env python3
"""Looks for remote values in what clients, files and logs hold. The secrets come from where they really are: the tokens
from the database of the remote, the item and server IDs from the stub registry of the consumer.

Usage: [REMOTE_PORT=<host port of the remote>] leak.py <folder with remote-db/ and federated-stubs.json> <file or folder>...
"""
import json
import os
import shutil
import sqlite3
import sys
import tempfile

work, targets = sys.argv[1], sys.argv[2:]

folder = tempfile.mkdtemp()
for name in os.listdir(work + "/remote-db"):
    shutil.copy(os.path.join(work, "remote-db", name), folder)
db = sqlite3.connect(folder + "/jellyfin.db")
tokens = {row[0] for row in db.execute("select AccessToken from Devices") if row[0]}
db.close()

stubs = json.load(open(work + "/federated-stubs.json"))
items, servers = set(), set()
for stub in stubs["Stubs"]:
    for source in stub["Sources"]:
        items.add(source["ItemId"])
        servers.add(source["ServerId"].replace("-", ""))
        servers.add(source["ServerId"])

port = os.environ.get("REMOTE_PORT", "18431")
fixed = [port, "host.docker.internal:" + port, "/media/film/Tears"]
print("secrets:", len(tokens), "tokens,", len(items), "items,", len(servers), "server ids")
assert tokens and items

files = []
for target in targets:
    if os.path.isdir(target):
        files += [os.path.join(root, name) for root, _, names in os.walk(target) for name in names]
    else:
        files.append(target)

hits = total = 0
for path in files:
    data = open(path, "rb").read()
    total += len(data)
    for label, values in (("token", tokens), ("item", items), ("server", servers), ("fixed", fixed)):
        # The ID of a server is minted here and says nothing about it: only clients must not see it.
        if label == "server" and path.endswith(".log"):
            continue
        for value in values:
            at = data.find(value.encode())
            # The folder of a server is named by its ID: in a federated path it is no secret (spec of the server folders).
            while label == "server" and at >= 0 and data[max(0, at - 10):at] == b"/federata/":
                at = data.find(value.encode(), at + 1)
            if at >= 0:
                print("HIT", label, value[:16], path.split("/")[-1], data[max(0, at - 60):at + 30])
                hits += 1
print(f"scanned {len(files)} files, {total} bytes, hits {hits}")
sys.exit(1 if hits else 0)
