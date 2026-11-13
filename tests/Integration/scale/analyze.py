#!/usr/bin/env python3
"""Turns the raw data of run.sh into the numbers of each phase.

usage: analyze.py <work folder>

A sync runs from LastStartedUtc to LastFinishedUtc of /JellyMesh/Admin/Sync. Inside it, the read ends with the plugin's
"Read N ... entries" log line, the index is saved when federation-index.json gets its new mtime, and the
materialization ends with the "Materialized the federated folder" line. The fake's request log says what was asked for
and how long the fake itself took to answer.
"""
import datetime
import json
import os
import re
import sys

WORK = sys.argv[1]
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


def report(phase, previous_files):
    status_text = read(f"{phase}-status.json")
    if not status_text:
        return None
    status = json.loads(status_text)
    start, end = iso_ms(status.get("LastStartedUtc")), iso_ms(status.get("LastFinishedUtc"))
    window = [r for r in requests if start - 1000 <= r[0] <= end + 1000]
    items = [r for r in window if r[3].startswith("items-")]
    images = [r for r in window if r[3].startswith("image-")]
    others = [r for r in window if not r[3].startswith(("items-", "image-"))]
    in_logs = [entry for entry in logs if start - 1000 <= entry[0] <= end + 1000]
    # One line per server: the read ends with the last.
    read_lines = [entry for entry in in_logs if re.match(r"Read \d+ .*entries", entry[3])]
    read_line = read_lines[-1] if read_lines else None
    done_line = next((entry for entry in in_logs if entry[3].startswith("Materialized the federated folder")), None)
    index = files_of(phase).get("federation-index.json")
    index_saved = index[1] if index and previous_files.get("federation-index.json") != index and start <= index[1] <= end + 1000 else None
    stubs = files_of(phase).get("federated-stubs.json")
    in_samples = [s for s in samples if start <= s[0] <= end]
    memory = memory_of(phase)
    tree = tree_of(phase)

    read_end = read_line[0] if read_line else (items[-1][0] if items else None)
    materialize_start = index_saved or read_end
    materialize_end = done_line[0] if done_line else end
    lines = [
        f"## {phase}",
        f"wall time (LastStartedUtc to LastFinishedUtc) {seconds(end - start)}",
        f"  read of the catalog                      {seconds(read_end - start) if read_end else '-'}"
        f"  ({len(items)} /Items calls, {sum(r[2] for r in items) / 1048576:.1f} MiB, fake busy {seconds(sum(r[1] for r in items))})",
        f"  index save after the read                {seconds(index_saved - read_end) if index_saved and read_end else 'no write'}",
        f"  materialization                          {seconds(materialize_end - materialize_start) if materialize_start else '-'}",
        f"    image downloads                        {len(images)}"
        + (f", first to last {seconds(images[-1][0] - images[0][0])}, fake busy {seconds(sum(r[1] for r in images))}" if images else ""),
        f"    from read end to first image           {seconds(images[0][0] - read_end) if images and read_end else '-'}",
        f"    after the last image                   {seconds(materialize_end - images[-1][0]) if images else '-'}",
        f"  after the materialization                {seconds(end - materialize_end) if done_line else '-'}  (version links, migration, libraries)",
        f"  other calls                              {len(others)} ({', '.join(sorted({r[3].split(' ')[0] for r in others}))})",
        f"log                                        {sum_reads(read_lines)} | {done_line[3] if done_line else '-'}",
        f"warnings/errors in the window              {sum(1 for e in in_logs if e[1] in ('WRN', 'ERR', 'FTL'))}",
    ]
    if in_samples:
        lines.append(f"memory, docker stats peak sample          {max(s[1] for s in in_samples)} MiB (at start {in_samples[0][1]} MiB)")
        lines.append(f"memory, process RSS peak sample           {max(s[4] for s in in_samples)} MiB")
    lines.append(f"memory, process VmHWM after the phase     {memory.get('VmHWM', '-')} MiB (cgroup memory.peak {memory.get('cgroup-peak', '-')} MiB, page cache included)")
    lines.append(f"federation-index.json                     {index[0] / 1048576:.1f} MiB" if index else "federation-index.json                     -")
    lines.append(f"federated-stubs.json                      {stubs[0] / 1048576:.1f} MiB" if stubs else "federated-stubs.json                      -")
    groups = (read(f"{phase}-groups.txt") or "").strip()
    if groups:
        lines.append(f"version groups in the registry            {groups}")
    links = (read(f"{phase}-links.txt") or "").strip()
    if links:
        lines.append(f"listed films and episodes by versions     {links}")
    lines.append(f"files in the federated folder             {tree.get('total', 0)} ({', '.join(f'{k} {v}' for k, v in sorted(tree.items()) if k not in ('total', 'kib'))}), {tree.get('kib', 0) / 1024:.0f} MiB")
    servers = status.get("Servers") or []
    lines.append("servers                                   " + "; ".join(
        f"{s.get('State')} failure={s.get('Failure')} movies={s.get('Movies')} series={s.get('Series')} episodes={s.get('Episodes')}" for s in servers))
    return "\n".join(lines), files_of(phase)


print(f"# Scale run in {WORK}")
print((read("fake.out") or read("fake-1.out") or "").strip())
remotes = len([name for name in os.listdir(WORK) if re.match(r"fake-\d+\.out$", name)])
if remotes > 1:
    print(f"{remotes} remote servers with that catalog each")
idle = memory_of("idle")
print(f"idle before the first sync: VmHWM {idle.get('VmHWM', '-')} MiB, VmRSS {idle.get('VmRSS', '-')} MiB\n")
previous = files_of("idle")
for phase in ("first", "incremental", "repeat", "nightly", "scan", "incremental-scanned", "incremental-scanned-again", "nightly-scanned"):
    if phase == "scan":
        if read("scan-seen"):
            requested, seen = int(read("scan-requested")), int(read("scan-seen"))
            memory = memory_of("scan")
            scan_samples = [s for s in samples if requested <= s[0] <= seen]
            print("## scan of the linked federated folder (Jellyfin's own work, with the plugin's providers)")
            print(f"wall time                                 {seconds(seen - requested)} (polled every 2 s)")
            print(f"items in the library                      {(read('scan-items.txt') or '-').strip()}")
            if scan_samples:
                print(f"memory, docker stats peak sample          {max(s[1] for s in scan_samples)} MiB")
            print(f"memory, process VmHWM after               {memory.get('VmHWM', '-')} MiB\n")
            previous = files_of("scan")
        continue
    result = report(phase, previous)
    if result:
        text, previous = result
        print(text + "\n")
