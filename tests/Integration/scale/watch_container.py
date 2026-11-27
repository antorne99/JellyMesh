#!/usr/bin/env python3
"""Ends the script that started it when the container is no longer the one that was running at the first look.

usage: watch_container.py <container> <reason file> [every s] [answer within s]

The container stopped, was started again, or Docker did not answer three times in a row: the reason goes to the file,
then the script gets a USR1 and everything it started a TERM.
"""
import os
import signal
import subprocess
import sys
import time

MISSES = 3


def look(container, within):
    """Returns "true <start time>" or "false <start time>", or None when Docker does not answer or knows no such container."""
    try:
        found = subprocess.run(["docker", "inspect", "--format", "{{.State.Running}} {{.State.StartedAt}}", container],
                               stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=within, text=True)
    except (subprocess.TimeoutExpired, OSError):
        return None
    return found.stdout.strip() if found.returncode == 0 else None


def wait_fall(container, every, within, parent):
    """Returns why the container is gone, or None when the script ended first."""
    first, misses = None, 0
    while os.getppid() == parent:
        now = look(container, within)
        misses = 0 if now else misses + 1
        first = first or now
        if misses == MISSES:
            return f"Docker did not answer for {container} {MISSES} times in a row"
        if now and now.startswith("false"):
            return f"{container} stopped"
        if now and now != first:
            return f"{container} was restarted"
        time.sleep(every)
    return None


def end(parent):
    """Sends a USR1 to the script and a TERM to its descendants, itself left out."""
    children = {}
    for line in subprocess.run(["ps", "-axo", "pid=,ppid="], stdout=subprocess.PIPE, text=True).stdout.splitlines():
        pid, above = map(int, line.split())
        children.setdefault(above, []).append(pid)
    tree = [parent]
    for pid in tree:
        tree += [child for child in children.get(pid, []) if child != os.getpid()]
    for pid in tree:
        try:
            os.kill(pid, signal.SIGUSR1 if pid == parent else signal.SIGTERM)
        except ProcessLookupError:
            pass


def main():
    container, reason_file = sys.argv[1:3]
    every = float(sys.argv[3]) if len(sys.argv) > 3 else 5
    within = float(sys.argv[4]) if len(sys.argv) > 4 else 20
    parent = os.getppid()
    reason = wait_fall(container, every, within, parent)
    if reason and os.getppid() == parent:
        with open(reason_file, "w") as out:
            out.write(time.strftime("%H:%M:%SZ ", time.gmtime()) + reason + "\n")
        end(parent)


if __name__ == "__main__":
    main()
