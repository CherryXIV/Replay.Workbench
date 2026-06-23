#!/usr/bin/env python3
"""
Pull the latest FFXIV Global ServerZoneIpcType opcodes from karashiiro/FFXIVOpcodes
and print them as an OPCODE_TABLES entry ready to paste into docs/opcodes.js.

Usage:
    python tools/update_opcodes.py

The script prints, to stdout:
  1. the patch version it found,
  2. the OPCODE_TABLES line (compact JSON, keys sorted alphabetically),

Paste the line into OPCODE_TABLES, then update BUILD_TO_PATCH, LATEST_PATCH,
and LATEST_GAME_BUILD by hand (the build number is not in opcodes.json).
"""

import json
import sys
import urllib.request

URL = "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/refs/heads/master/opcodes.json"
REGION = "Global"
LIST_NAME = "ServerZoneIpcType"


def fetch(url):
    with urllib.request.urlopen(url) as resp:
        return json.load(resp)


def main():
    data = fetch(URL)

    # The file is an array of region blocks; find the Global one.
    block = next((b for b in data if b.get("region") == REGION), None)
    if block is None:
        sys.exit(f"No region '{REGION}' found in opcodes.json")

    version = block.get("version", "UNKNOWN")
    entries = block.get("lists", {}).get(LIST_NAME)
    if not entries:
        sys.exit(f"No '{LIST_NAME}' list found for region '{REGION}'")

    # Build {name: opcode}, sorted by name to match the existing tables.
    table = {e["name"]: e["opcode"] for e in entries}
    table = dict(sorted(table.items()))

    # Compact JSON: no spaces after ':' or ',' — matches docs/opcodes.js style.
    compact = json.dumps(table, separators=(",", ":"))

    print(f"// patch version reported by opcodes.json: {version}")
    print(f"// {len(table)} opcodes")
    print()
    print(f'  "{version}": {compact},')


if __name__ == "__main__":
    main()
