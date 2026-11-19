#!/usr/bin/env python3
"""Turns the raw data of run.sh into the numbers of each phase.

usage: analyze.py <work folder>

A sync runs from LastStartedUtc to LastFinishedUtc of /JellyMesh/Admin/Sync. Inside it, the read ends with the plugin's
"Read N ... entries" log line, and the materialization ends with the "Materialized the federated folder" line, or with
"Nothing changed since the last sync" when the sync skips it and the version links. The index is saved in the plugin's
SQLite store (jellymesh.db and its -wal log), whose files say nothing about when, so the materialization counts from the
end of the read with the index save in it. The fake's request log says what was asked for
and how long the fake itself took to answer. With music, the fakes' artist pages count as reads, and music-expected.txt
says what the federation should hold.
"""
import datetime
import json
import os
import re
import sys

WORK = sys.argv[1]
# The request kinds of a catalog read: /Items pages, and the artist pages of the music.
READS = ("items-", "artists ", "album-artists ")
LOG_LINE = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) ([+-]\d\d:\d\d)\] \[(\w+)\] \[\d+\] ([\w.]+): (.*)")


def path(name):
    return os.path.join(WORK, name)


def read(name):
    try:
        with open(path(name)) as handle:
            return handle.read()
    except OSError:
        return None


def iso_ms(value):
    if not value:
        return None
    # Python before 3.11 reads only 6 fraction digits, and .NET drops trailing zeros.
    value = re.sub(r"\.(\d+)", lambda m: "." + (m.group(1) + "000000")[:6], value).replace("Z", "+00:00")
    return int(datetime.datetime.fromisoformat(value).timestamp() * 1000)


def log_ms(day, offset):
    return int(datetime.datetime.fromisoformat(f"{day[:23]}{offset}").timestamp() * 1000)


def seconds(ms):
    return "-" if ms is None else f"{ms / 1000:.1f} s"


requests = []
for line in (read("requests.log") or "").splitlines():
    parts = line.split(" ", 3)
    if len(parts) == 4:
        requests.append((int(parts[0]), int(parts[1]), int(parts[2]), parts[3]))

samples = []
for line in (read("samples.txt") or "").splitlines():
    parts = line.split()
    if len(parts) >= 5:
        samples.append((int(parts[0]), int(parts[1]), parts[2], int(parts[3]), int(parts[4])))

logs = []
for line in (read("jellyfin.log") or "").splitlines():
    match = LOG_LINE.match(line)
    if match:
        logs.append((log_ms(match.group(1), match.group(2)), match.group(3), match.group(4), match.group(5)))


def files_of(phase):
    found = {}
    for line in (read(f"{phase}-files.txt") or "").splitlines():
        name, size, mtime = line.split()
        found[name] = (int(size), int(float(mtime) * 1000))
    return found


def tree_of(phase):
    text = read(f"{phase}-tree.txt") or ""
    counts = {}
    for line in text.splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[0].isdigit():
            counts[parts[1]] = int(parts[0])
        elif len(parts) == 2:
            counts[parts[0]] = int(parts[1])
    return counts


def memory_of(phase):
    values = {}
    for line in (read(f"{phase}-memory.txt") or "").splitlines():
        name, value = line.split()
        values[name.rstrip(":")] = int(value)
    return values


def sum_reads(read_lines):
    if not read_lines:
        return "-"
    if len(read_lines) == 1:
        return read_lines[0][3]
    counts = [int(re.match(r"Read (\d+)", entry[3]).group(1)) for entry in read_lines]
    return f"Read {' + '.join(map(str, counts))} entries from {len(read_lines)} servers"


def report(phase):
    status_text = read(f"{phase}-status.json")
    if not status_text:
        return None
    status = json.loads(status_text)
    start, end = iso_ms(status.get("LastStartedUtc")), iso_ms(status.get("LastFinishedUtc"))
    window = [r for r in requests if start - 1000 <= r[0] <= end + 1000]
    items = [r for r in window if r[3].startswith(READS)]
    images = [r for r in window if r[3].startswith("image-")]
    others = [r for r in window if not r[3].startswith(READS + ("image-",))]
    unknown = [r for r in others if r[3].startswith("unknown ")]
    in_logs = [entry for entry in logs if start - 1000 <= entry[0] <= end + 1000]
    # One line per server: the read ends with the last.
    read_lines = [entry for entry in in_logs if re.match(r"Read \d+ .*entries", entry[3])]
    read_line = read_lines[-1] if read_lines else None
    done_line = next((entry for entry in in_logs if entry[3].startswith(("Materialized the federated folder", "Nothing changed since the last sync"))), None)
    link_line = next((entry for entry in in_logs if entry[3].startswith("Linked the versions of the titles")), None)
    in_samples = [s for s in samples if start <= s[0] <= end]
    memory = memory_of(phase)
    tree = tree_of(phase)

    read_end = read_line[0] if read_line else (items[-1][0] if items else None)
    materialize_start = read_end
    materialize_end = done_line[0] if done_line else end
    lines = [
        f"## {phase}",
        f"wall time (LastStartedUtc to LastFinishedUtc) {seconds(end - start)}",
        f"  read of the catalog                      {seconds(read_end - start) if read_end else '-'}"
        f"  ({len(items)} /Items calls, {sum(r[2] for r in items) / 1048576:.1f} MiB, fake busy {seconds(sum(r[1] for r in items))})",
    ]
    kinds = {}
    for r in items:
        kinds[r[3].split(" ")[0]] = kinds.get(r[3].split(" ")[0], 0) + 1
    if any(not kind.startswith(("items-Movie", "items-Series", "items-Season", "items-Episode")) for kind in kinds):
        lines.append(f"    calls by kind                          {', '.join(f'{k} {v}' for k, v in sorted(kinds.items()))}")
    # A full read of each server starts with the first page of the type it reads first, so the pages split by server there.
    segments = []
    first_page = next((r[3].split(" ")[0] + " 0" for r in items if r[3].endswith(" 0")), None)
    for r in items:
        if r[3] == first_page or not segments:
            segments.append([])
        segments[-1].append(r)
    if len(segments) > 1:
        for n, segment in enumerate(segments, 1):
            end_ms = segment[-1][0] + segment[-1][1]
            following = segments[n][0][0] if n < len(segments) else read_end
            lines.append(f"    server {n}: pages {seconds(end_ms - segment[0][0])} ({len(segment)} calls, "
                         f"{(end_ms - segment[0][0]) / len(segment):.0f} ms a call, fake busy {seconds(sum(r[1] for r in segment))}), "
                         f"then {seconds(following - end_ms) if following else '-'} to the {'next server' if n < len(segments) else 'read log line'}")
    lines += [
        f"  materialization, with the index save     {seconds(materialize_end - materialize_start) if materialize_start else '-'}",
        f"    image downloads                        {len(images)}"
        + (f", first to last {seconds(images[-1][0] - images[0][0])}, fake busy {seconds(sum(r[1] for r in images))}" if images else ""),
        f"    from read end to first image           {seconds(images[0][0] - read_end) if images and read_end else '-'}",
        f"    after the last image                   {seconds(materialize_end - images[-1][0]) if images else '-'}",
        f"  after the materialization                {seconds(end - materialize_end) if done_line else '-'}  (version links, migration, libraries)",
        f"  other calls                              {len(others)} ({', '.join(sorted({r[3].split(' ')[0] for r in others}))})",
    ]
    if unknown:
        lines.append(f"  UNKNOWN CALLS, answered 404 by the fake  {len(unknown)}: {', '.join(sorted({r[3] for r in unknown}))}")
    lines += [
        f"log                                        {sum_reads(read_lines)} | {done_line[3] if done_line else '-'}",
        f"version links                              {link_line[3] if link_line else '-'}",
        f"warnings/errors in the window              {sum(1 for e in in_logs if e[1] in ('WRN', 'ERR', 'FTL'))}",
    ]
    if in_samples:
        lines.append(f"memory, docker stats peak sample          {max(s[1] for s in in_samples)} MiB (at start {in_samples[0][1]} MiB)")
        lines.append(f"memory, process RSS peak sample           {max(s[4] for s in in_samples)} MiB")
    lines.append(f"memory, process VmHWM after the phase     {memory.get('VmHWM', '-')} MiB (cgroup memory.peak {memory.get('cgroup-peak', '-')} MiB, page cache included)")
    files = files_of(phase)
    database, log = files.get("jellymesh.db"), files.get("jellymesh.db-wal")
    lines.append(f"jellymesh.db                              {database[0] / 1048576:.1f} MiB" if database else "jellymesh.db                              -")
    lines.append(f"jellymesh.db-wal                          {log[0] / 1048576:.1f} MiB" if log else "jellymesh.db-wal                          -")
    groups = (read(f"{phase}-groups.txt") or "").strip()
    if groups:
        lines.append(f"version groups in the registry            {groups}")
    links = (read(f"{phase}-links.txt") or "").strip()
    if links:
        lines.append(f"listed films and episodes by versions     {links}")
    track_links = (read(f"{phase}-track-links.txt") or "").strip()
    if track_links:
        lines.append(f"listed tracks by versions                 {track_links}")
    lines.append(f"files in the federated folder             {tree.get('total', 0)} ({', '.join(f'{k} {v}' for k, v in sorted(tree.items()) if k not in ('total', 'kib'))}), {tree.get('kib', 0) / 1024:.0f} MiB")
    servers = status.get("Servers") or []
    lines.append("servers                                   " + "; ".join(
        f"{s.get('State')} failure={s.get('Failure')} movies={s.get('Movies')} series={s.get('Series')} episodes={s.get('Episodes')}"
        + "".join(f" {key.lower()}={s[key]}" for key in ("Tracks", "Albums", "Artists") if key in s) for s in servers))
    return "\n".join(lines)


print(f"# Scale run in {WORK}")
print((read("fake.out") or read("fake-1.out") or "").strip())
expected = (read("music-expected.txt") or "").strip()
if expected:
    print(f"expected: {expected}")
remotes = len([name for name in os.listdir(WORK) if re.match(r"fake-\d+\.out$", name)])
if remotes > 1:
    print(f"{remotes} remote servers with that catalog each")
idle = memory_of("idle")
print(f"idle before the first sync: VmHWM {idle.get('VmHWM', '-')} MiB, VmRSS {idle.get('VmRSS', '-')} MiB\n")
for phase in ("first", "incremental", "repeat", "nightly", "scan", "incremental-scanned", "incremental-scanned-again",
              "incremental-scanned-third", "nightly-scanned", "incremental-after-nightly"):
    if phase == "scan":
        if read("scan-seen"):
            requested, seen = int(read("scan-requested")), int(read("scan-seen"))
            memory = memory_of("scan")
            scan_samples = [s for s in samples if requested <= s[0] <= seen]
            print("## scan of the linked federated folder (Jellyfin's own work, with the plugin's providers)")
            print(f"wall time                                 {seconds(seen - requested)} (polled every 2 s)")
            print(f"items in the library                      {(read('scan-items.txt') or '-').strip()}")
            music = (read("scan-music-items.txt") or read("scan-music.txt") or "").strip()
            if music:
                print(f"music in the library                      {'; '.join(music.splitlines())}")
            if scan_samples:
                print(f"memory, docker stats peak sample          {max(s[1] for s in scan_samples)} MiB")
            print(f"memory, process VmHWM after               {memory.get('VmHWM', '-')} MiB\n")
        continue
    text = report(phase)
    if text:
        print(text + "\n")
