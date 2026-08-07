#!/usr/bin/env python3
"""
Convert a pasted C# ServerZoneIpcType enum into an OPCODE_TABLES entry ready to
paste into docs/opcodes.js.

Companion to update_opcodes.py, for when karashiiro/FFXIVOpcodes hasn't merged
the new patch yet but the .cs enum is available (an open PR, a fork, Discord).
Same output shape as update_opcodes.py, so the paste step is identical.

Usage:
    python tools/convert_ipc_enum.py enum.txt
    python tools/convert_ipc_enum.py enum.txt --patch 7.55h --party-list 383 \
        --party-portrait 424 --build 13740000
    ... | python tools/convert_ipc_enum.py -

Input is the enum body; the surrounding `enum ServerZoneIpcType : ushort {` and
braces are optional, and trailing `// 7.55h` comments are used to guess the patch
name. Values may be hex (0x024D) or decimal — output is always decimal, because
that is what the site's tables use.

PartyList and PartyPortraitInfo have no IPC name upstream, so they are never in
the enum. They are emitted as null (which the site treats as "unknown") unless
you pass them; find them with:
    python tools/find_partylist_opcode.py REPLAY.dat
"""

import argparse
import collections
import json
import re
import sys

# Packets the site needs but that carry no name in any IPC enum. Kept at the
# front of the table, matching the existing entries in docs/opcodes.js.
MANUAL_NAMES = ["PartyList", "PartyPortraitInfo"]

PREFERRED_ENUM = "ServerZoneIpcType"

ENUM_RE = re.compile(r"\benum\s+(\w+)")
ENTRY_RE = re.compile(
    r"^\s*(?P<name>[A-Za-z_]\w*)\s*=\s*(?P<value>0[xX][0-9a-fA-F]+|\d+)\s*,?\s*(?://.*)?$"
)
PATCH_RE = re.compile(r"//\s*v?(\d+\.\d+[A-Za-z0-9.]*)")


def parse(text):
    """-> ({enum_name: {ipc_name: opcode}}, [patch strings seen], [skipped lines])"""
    tables = collections.OrderedDict()
    patches = []
    skipped = []
    current = ""  # entries before any `enum X` header land in the unnamed bucket

    for lineno, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if not stripped or stripped in "{};" or stripped.startswith("//"):
            continue

        header = ENUM_RE.search(line)
        if header:
            current = header.group(1)
            tables.setdefault(current, collections.OrderedDict())
            continue

        m = ENTRY_RE.match(line)
        if not m:
            skipped.append((lineno, stripped))
            continue

        patch = PATCH_RE.search(line)
        if patch:
            patches.append(patch.group(1))

        value = m.group("value")
        tables.setdefault(current, collections.OrderedDict())
        tables[current][m.group("name")] = int(value, 16 if value[:2].lower() == "0x" else 10)

    return tables, patches, skipped


def pick_enum(tables, wanted):
    """Choose which parsed enum to emit, preferring ServerZoneIpcType."""
    if wanted:
        if wanted not in tables:
            sys.exit(f"No enum named '{wanted}' in the input (found: {', '.join(k or '<unnamed>' for k in tables)})")
        return wanted, tables[wanted]
    if PREFERRED_ENUM in tables:
        return PREFERRED_ENUM, tables[PREFERRED_ENUM]
    if len(tables) == 1:
        return next(iter(tables.items()))
    sys.exit(
        f"Input holds several enums ({', '.join(k or '<unnamed>' for k in tables)}); "
        f"pick one with --enum"
    )


def collisions(table):
    """Two names on one opcode make transpose refuse the file — flag it here first."""
    by_op = collections.defaultdict(list)
    for name, op in table.items():
        if op is not None:
            by_op[op].append(name)
    return {op: names for op, names in by_op.items() if len(names) > 1}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="text file holding the C# enum, or '-' for stdin")
    ap.add_argument("--enum", help=f"which enum to convert (default: {PREFERRED_ENUM})")
    ap.add_argument("--patch", help="patch key for the table (default: guessed from // comments)")
    ap.add_argument("--build", type=int, help="game build number, for the BUILD_TO_PATCH line")
    ap.add_argument("--party-list", type=int, help="PartyList opcode (decimal)")
    ap.add_argument("--party-portrait", type=int, help="PartyPortraitInfo opcode (decimal)")
    ap.add_argument("--json", action="store_true", help="print only the compact JSON object")
    args = ap.parse_args()

    text = sys.stdin.read() if args.input == "-" else open(args.input, encoding="utf-8-sig").read()

    tables, patches, skipped = parse(text)
    if not tables:
        sys.exit("No enum entries found in the input")
    enum_name, entries = pick_enum(tables, args.enum)
    if not entries:
        sys.exit(f"Enum '{enum_name}' has no entries")

    patch = args.patch
    if not patch and patches:
        patch = collections.Counter(patches).most_common(1)[0][0]
    if not patch:
        patch = "UNKNOWN"

    # Manual names first, then everything else alphabetically — the layout the
    # existing docs/opcodes.js entries use.
    table = collections.OrderedDict()
    table["PartyList"] = args.party_list
    table["PartyPortraitInfo"] = args.party_portrait
    for name in sorted(entries):
        if name not in MANUAL_NAMES:
            table[name] = entries[name]

    compact = json.dumps(table, separators=(",", ":"))
    if args.json:
        print(compact)
        return

    print(f"// {enum_name}, patch {patch}")
    print(f"// {len(entries)} named opcodes + {len(MANUAL_NAMES)} manual")
    if skipped:
        print(f"// {len(skipped)} line(s) skipped, first: L{skipped[0][0]} {skipped[0][1]!r}")
    dupes = collisions(table)
    if dupes:
        print("// WARNING: duplicate opcodes — transpose refuses tables like this:")
        for op, names in sorted(dupes.items()):
            print(f"//   {op} = {' + '.join(names)}")
    missing = [n for n in MANUAL_NAMES if table[n] is None]
    if missing:
        print(f"// {', '.join(missing)} unset - run tools/find_partylist_opcode.py on a "
              f"{patch} replay and fill them in")
    print()
    print("// 1. add to OPCODE_TABLES:")
    print(f'  "{patch}": {compact},')
    print()
    print("// 2. add to BUILD_TO_PATCH:")
    print(f'  {args.build if args.build else "<build>"}: "{patch}",')
    print()
    print("// 3. bump the header:")
    print(f'  LATEST_PATCH = "{patch}";')
    print(f'  LATEST_GAME_BUILD = {args.build if args.build else "<build>"};')


if __name__ == "__main__":
    main()
