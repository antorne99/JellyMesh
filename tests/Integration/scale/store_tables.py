#!/usr/bin/env python3
"""Prints each table of a JellyMesh store as "<table> <rows> <digest>", the digest over its rows in key order.

usage: store_tables.py <jellymesh.db>

Two prints that differ on a table say that the store wrote it in between (row 386, D27).
"""
import hashlib
import sqlite3
import sys


def main():
    connection = sqlite3.connect(sys.argv[1])
    tables = [row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
    for table in tables:
        digest, rows = hashlib.sha256(), 0
        for row in connection.execute(f'SELECT * FROM "{table}" ORDER BY 1'):
            digest.update(repr(row).encode())
            rows += 1
        print(table, rows, digest.hexdigest()[:16])


if __name__ == "__main__":
    main()
