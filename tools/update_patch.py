#!/usr/bin/env python3
"""One command to bring the workbench onto a new game patch.

Run this when a patch or hotfix ships and xivdev/opcodediff has published the
diff for it:

    python tools/update_patch.py --build 13860000
    python tools/update_patch.py --from-replay "C:/path/to/fresh recording.dat"

It does, in order:

  1. mirrors opcodediff's diffs/ into tools/diffs (downloads only what's new),
  2. appends any patch newer than the current chain tail to VERSION_CHAIN in
     tools/build_patchdiffs.py and FALLBACK_CHAIN in tools/bump_replay.py,
  3. regenerates docs/patchdiffs.js from the diffs,
  4. derives the new patch's IPC name table by carrying the previous patch's
     table forward through the new hop, and adds it to OPCODE_TABLES,
  5. with a build number: bumps LATEST_PATCH / LATEST_GAME_BUILD, extends
     BUILD_TO_PATCH in docs/opcodes.js (and docs/old/opcodes.js) and adds the
     build to tools/replay_builds.json.

Every step is idempotent and reports "already current" when there is nothing to
do, so it is safe to re-run -- which is the point, because the two halves of an
update usually arrive on different days: opcodediff publishes the diff within
hours of a patch, but the build number can only be read out of a recording made
on the new client. Run it without --build the moment the diff lands, then again
with --build once you have a recording.

Names are DERIVED, not downloaded. Carrying the previous table forward through
the diff reproduces the published list exactly (verified against 7.55 -> 7.55h,
172/172) while keeping the hand-corrections this repo has made -- PartyList and
PartyPortraitInfo have both been wrong in the third-party name dump before.
--verify-names cross-checks against that dump anyway and reports disagreements
without acting on them; --merge-new-names additionally adopts names it has that
the derivation could not produce (packets added by the patch itself).

Other flags:
    --check            report what would change, write nothing
    --to PATCH         stop at PATCH instead of the newest diff available
    --diffs DIR        use a local opcodediff diffs/ instead of downloading
    --no-old           leave docs/old/opcodes.js alone
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import struct
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_patchdiffs  # noqa: E402
from bump_replay import (  # noqa: E402
    Fatal,
    NON_IPC_OPCODE,
    OFF_BUILD,
    load_hop,
    opcode_histogram,
    read_replay,
    segment_offsets,
)

TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(TOOLS_DIR)
MIRROR_DIR = os.path.join(TOOLS_DIR, "diffs")
OPCODES_JS = os.path.join(REPO_ROOT, "docs", "opcodes.js")
OLD_OPCODES_JS = os.path.join(REPO_ROOT, "docs", "old", "opcodes.js")
BUILDS_JSON = os.path.join(TOOLS_DIR, "replay_builds.json")
BUILD_PATCHDIFFS_PY = os.path.join(TOOLS_DIR, "build_patchdiffs.py")
BUMP_REPLAY_PY = os.path.join(TOOLS_DIR, "bump_replay.py")

DIFFS_API = "https://api.github.com/repos/xivdev/opcodediff/contents/diffs"
DIFFS_RAW = "https://raw.githubusercontent.com/xivdev/opcodediff/main/diffs/{}"
NAMES_URL = "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/refs/heads/master/opcodes.json"
UA = {"User-Agent": "Replay.Workbench-update_patch"}

# Names the workbench looks up rather than just labelling with. If the diff drops
# one of these the tool quietly loses a feature, so they get called out by name.
CRITICAL_NAMES = [
    "NpcSpawn", "PlayerSpawn", "PlaceFieldMarker", "PlaceFieldMarkerPreset",
    "PartyList", "PartyPortraitInfo", "FirstAttack", "ActorCast", "Effect",
    "AoeEffect8", "AoeEffect16", "AoeEffect24", "AoeEffect32",
]


# =====================================================================
# Small helpers
# =====================================================================
def read_text(path: str) -> str:
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def write_text(path: str, text: str) -> None:
    # CRLF throughout, matching .gitattributes (* text=auto eol=crlf).
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(text)


VERSION_RE = re.compile(r"^(\d+)\.(\d+)([a-z]*)(\d*)$")


def version_key(version: str):
    """Sort key for patch names: 7.55 < 7.55h < 7.55h2 < 7.76 < 8.00.

    opcodediff writes the minor with however many digits the patch had a name
    for -- 6.3 and 6.30h are the same patch line -- so the minor is padded to two
    digits before comparing. Read as a plain integer, "7.56" would sort below
    "7.55" and a whole patch would be skipped without a word.
    """
    m = VERSION_RE.match(version)
    if not m:
        return (99, 99, "zz", 99)
    major, minor, suffix, num = m.groups()
    return (int(major), int(minor.ljust(2, "0")), suffix, int(num) if num else (1 if suffix else 0))


def fetch(url: str, timeout: int = 30) -> bytes:
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


# =====================================================================
# Step 1 - mirror opcodediff's diffs/
# =====================================================================
def mirror_versions(mirror: str) -> list[str]:
    if not os.path.isdir(mirror):
        return []
    return sorted(
        (f[: -len(".diff.json")] for f in os.listdir(mirror) if f.endswith(".diff.json")),
        key=version_key,
    )


def sync_diffs(mirror: str, floor: str, local: str | None, log) -> list[str]:
    """Fill `mirror` with every diff from `floor` (7.00) upward.

    Copies from a local opcodediff checkout when one is around -- that is 2.5 MB
    of downloads saved on a fresh clone -- then fetches whatever is still
    missing. Only files that are absent are touched, so the usual run after a
    hotfix pulls exactly one file.
    """
    os.makedirs(mirror, exist_ok=True)
    have = set(mirror_versions(mirror))

    if local:
        copied = []
        for name in sorted(os.listdir(local)):
            if not name.endswith(".diff.json"):
                continue
            version = name[: -len(".diff.json")]
            if version in have or version_key(version) < version_key(floor):
                continue
            shutil.copyfile(os.path.join(local, name), os.path.join(mirror, name))
            have.add(version)
            copied.append(version)
        if copied:
            log(f"copied {len(copied)} diff(s) from {local}")

    try:
        listing = json.loads(fetch(DIFFS_API))
    except (urllib.error.URLError, OSError, ValueError) as e:
        if not have:
            raise Fatal(f"could not list opcodediff's diffs/ ({e}) and tools/diffs is empty")
        log(f"warning: could not reach GitHub ({e}); working from the {len(have)} mirrored diff(s)")
        return mirror_versions(mirror)

    remote = {}
    for entry in listing:
        name = entry.get("name", "")
        if name.endswith(".diff.json"):
            remote[name[: -len(".diff.json")]] = entry.get("download_url") or DIFFS_RAW.format(name)

    wanted = [v for v in remote if version_key(v) >= version_key(floor)]
    missing = sorted((v for v in wanted if v not in have), key=version_key)
    for version in missing:
        data = fetch(remote[version])
        json.loads(data)  # a truncated download must not become a silent bad hop
        with open(os.path.join(mirror, f"{version}.diff.json"), "wb") as f:
            f.write(data)
        log(f"downloaded {version}.diff.json ({len(data):,} bytes)")
    if not missing:
        log(f"mirror already has every published diff ({len(wanted)} from {floor} up)")
    return mirror_versions(mirror)


# =====================================================================
# Step 2 - the patch chain
# =====================================================================
def hop_alignment(mirror: str, previous: str, version: str) -> str:
    """Does this hop actually start where the previous patch ended?

    The chain is ordered by version name, which is a guess about release order.
    A hop whose old side is the previous patch's new side confirms the guess;
    one that does not means the diffs arrived out of order and the chain would
    carry every recording onto the wrong packets.
    """
    hop = load_hop(mirror, version)
    prev = load_hop(mirror, previous)
    prev_news = set(prev["map"].values())
    if not prev_news:
        return "previous hop is empty; cannot check alignment"
    overlap = len(prev_news & hop["known"])
    pct = 100.0 * overlap / len(prev_news)
    verdict = "lines up with" if pct >= 95.0 else "DOES NOT line up with"
    return f"{overlap}/{len(prev_news)} opcodes ({pct:.1f}%) {verdict} {previous}"


def chain_literal_bounds(text: str, name: str) -> tuple[int, int]:
    m = re.search(rf"^{name}\s*=\s*\[", text, re.M)
    if not m:
        raise Fatal(f"no {name} list found")
    end = text.index("]", m.end())
    return m.end(), end


def extend_chain_literal(path: str, name: str, additions: list[str]) -> None:
    """Append versions to a chain literal, keeping its hand-made line grouping.

    The lists are grouped by minor version on purpose (one line per 7.x family),
    so this adds to the last line and wraps only when it would run long, instead
    of reflowing the whole block.
    """
    text = read_text(path)
    start, end = chain_literal_bounds(text, name)
    lines = text[start:end].split("\n")
    # Last line holding an entry; anything after it is the closing bracket's line.
    idx = max(i for i, line in enumerate(lines) if '"' in line)
    indent = re.match(r"\s*", lines[idx]).group(0)
    row = lines[idx].rstrip()
    if not row.endswith(","):
        row += ","
    for version in additions:
        piece = f' "{version}",'
        if len(row) + len(piece) > 92:
            lines[idx] = row
            idx += 1
            row = f'{indent}"{version}",'
            lines.insert(idx, row)
        else:
            row += piece
    lines[idx] = row
    write_text(path, text[:start] + "\n".join(lines) + text[end:])


def regenerate_patchdiffs(chain: list[str], mirror: str, log) -> None:
    build_patchdiffs.VERSION_CHAIN = chain
    hops, warnings = build_patchdiffs.build(mirror)
    text = build_patchdiffs.render(hops)
    write_text(build_patchdiffs.DEFAULT_OUT, text)
    log(f"regenerated docs/patchdiffs.js ({len(hops)} hops, {chain[0]} -> {chain[-1]})")
    for w in warnings:
        log("note:" + w)


# =====================================================================
# Step 3 - docs/opcodes.js surgery
# =====================================================================
def literal_bounds(text: str, decl: str) -> tuple[int, int]:
    """Span of the object/array literal assigned by `decl` (e.g. 'const FOO')."""
    i = text.index(decl)
    j = text.index("=", i) + 1
    while text[j] in " \t\r\n":
        j += 1
    open_ch = text[j]
    close_ch = {"[": "]", "{": "}"}[open_ch]
    depth, k, in_str = 0, j, False
    while k < len(text):
        c = text[k]
        if in_str:
            if c == "\\":
                k += 2
                continue
            if c == '"':
                in_str = False
        elif c == '"':
            in_str = True
        elif c == open_ch:
            depth += 1
        elif c == close_ch:
            depth -= 1
            if depth == 0:
                return j, k + 1
        k += 1
    raise Fatal(f"{decl}: literal is unterminated")


TRAILING_COMMA = re.compile(r",(\s*[}\]])")


def read_tables(text: str) -> dict[str, dict[str, int]]:
    start, end = literal_bounds(text, "const OPCODE_TABLES")
    return json.loads(TRAILING_COMMA.sub(r"\1", text[start:end]))


def insert_table(text: str, patch: str, table: dict[str, int]) -> str:
    start, end = literal_bounds(text, "const OPCODE_TABLES")
    body = text[start:end]
    entries = list(re.finditer(r"^([ \t]*)\"[^\"]+\":", body, re.M))
    indent = entries[-1].group(1) if entries else "\t"
    compact = json.dumps(table, separators=(",", ":"))
    line = f'{indent}"{patch}": {compact},\n'
    close = start + body.rindex("}")
    line_start = text.rindex("\n", start, close) + 1
    return text[:line_start] + line + text[line_start:]


def set_build_to_patch(text: str, build: int, patch: str) -> str:
    start, end = literal_bounds(text, "const BUILD_TO_PATCH")
    pairs = {int(b): p for b, p in re.findall(r"(\d+)\s*:\s*\"([^\"]+)\"", text[start:end])}
    pairs[build] = patch
    body = "{ " + ", ".join(f'{b}: "{pairs[b]}"' for b in sorted(pairs)) + " }"
    return text[:start] + body + text[end:]


def set_latest(text: str, patch: str, build: int) -> str:
    text, n1 = re.subn(r"^(\s*(?:let|const)\s+LATEST_PATCH\s*=\s*\")[^\"]*(\")",
                       rf"\g<1>{patch}\g<2>", text, count=1, flags=re.M)
    text, n2 = re.subn(r"^(\s*(?:let|const)\s+LATEST_GAME_BUILD\s*=\s*)\d+",
                       rf"\g<1>{build}", text, count=1, flags=re.M)
    if not (n1 and n2):
        raise Fatal("could not find LATEST_PATCH / LATEST_GAME_BUILD to update")
    return text


# =====================================================================
# Step 4 - names
# =====================================================================
def carry_names(table: dict[str, int], hop: dict) -> tuple[dict[str, int], list[tuple[str, int, str]]]:
    """Move a patch's name table onto the next patch's opcodes through one hop.

    Pseudo-opcodes (>= 0xf000, e.g. RSVPacket) are not in the game's IPC vtable
    and so never appear in a diff; they are fixed values and carry across as-is.
    """
    out, lost = {}, []
    for name, opcode in table.items():
        if opcode >= NON_IPC_OPCODE:
            out[name] = opcode
            continue
        new = hop["map"].get(opcode)
        if new is not None:
            out[name] = new
        else:
            why = ("could not be told apart" if opcode in hop["ambiguous"]
                   else "was removed" if opcode in hop["removed"]
                   else "is absent from the diff")
            lost.append((name, opcode, why))
    return out, lost


def collisions(table: dict[str, int]) -> dict[int, list[str]]:
    by_op: dict[int, list[str]] = {}
    for name, opcode in table.items():
        by_op.setdefault(opcode, []).append(name)
    return {op: names for op, names in by_op.items() if len(names) > 1}


def fetch_published_names(patch: str, log) -> dict[str, int] | None:
    """The third-party name dump for `patch`, or None if it hasn't caught up."""
    try:
        data = json.loads(fetch(NAMES_URL))
    except (urllib.error.URLError, OSError, ValueError) as e:
        log(f"cross-check skipped: could not fetch FFXIVOpcodes ({e})")
        return None
    block = next((b for b in data if b.get("region") == "Global"), None)
    if block is None:
        log("cross-check skipped: no Global region in FFXIVOpcodes")
        return None
    if block.get("version") != patch:
        log(f"cross-check skipped: FFXIVOpcodes is still on {block.get('version')!r}, not {patch}")
        return None
    entries = block.get("lists", {}).get("ServerZoneIpcType") or []
    return {e["name"]: e["opcode"] for e in entries}


def cross_check(derived: dict[str, int], published: dict[str, int], log, limit: int = 12) -> dict[str, int]:
    """Report derived-vs-published disagreements. Returns names only they have."""
    shared = set(derived) & set(published)
    disagree = sorted(n for n in shared if derived[n] != published[n])
    only_theirs = {n: published[n] for n in sorted(set(published) - set(derived))}
    only_ours = sorted(set(derived) - set(published))
    log(f"cross-check: {len(shared) - len(disagree)}/{len(shared)} shared names agree")
    for name in disagree[:limit]:
        log(f"  differs: {name} derived={derived[name]} published={published[name]}")
    if len(disagree) > limit:
        log(f"  ... +{len(disagree) - limit} more")
    if only_theirs:
        log(f"  {len(only_theirs)} name(s) only in the published list: "
            + ", ".join(list(only_theirs)[:8]) + (" ..." if len(only_theirs) > 8 else ""))
    if only_ours:
        log(f"  {len(only_ours)} name(s) only in ours: "
            + ", ".join(only_ours[:8]) + (" ..." if len(only_ours) > 8 else ""))
    return only_theirs


# =====================================================================
# Build number
# =====================================================================
def build_from_replay(path: str, log) -> tuple[int, dict[int, int]]:
    data = read_replay(path)
    build = struct.unpack_from("<i", data, OFF_BUILD)[0]
    hist = opcode_histogram(data, segment_offsets(data))
    log(f"{os.path.basename(path)}: build {build}, {sum(hist.values()):,} packets, {len(hist)} opcode kinds")
    return build, hist


def confirm_with_replay(hist: dict[int, int], chain: list[str], mirror: str, patch: str, log) -> None:
    """Does the recording's own opcode set actually fit the patch we just added?

    A recording made on the new client should be 100% accounted for by the new
    patch's vtable and by nothing else. Anything less means the chain or the
    diff is wrong, and it is far better to hear that now than after exporting a
    replay that crashes the client.
    """
    hops = {v: load_hop(mirror, v) for v in chain[1:]}
    universe = set(hops[patch]["map"].values())
    ipc = {o: c for o, c in hist.items() if o < NON_IPC_OPCODE}
    if not ipc:
        log("recording has no IPC packets to check against")
        return
    kinds = sum(1 for o in ipc if o in universe)
    packets = sum(c for o, c in ipc.items() if o in universe)
    log(f"recording vs {patch}: {kinds}/{len(ipc)} opcode kinds "
        f"({100.0 * packets / sum(ipc.values()):.1f}% of packets) are in its vtable")
    if kinds != len(ipc):
        strays = sorted(o for o in ipc if o not in universe)[:12]
        log("  unaccounted opcodes: " + ", ".join(f"0x{o:x}" for o in strays))
        log(f"  a fresh {patch} recording should be 100% -- check the build number and the diff")


# =====================================================================
# Main
# =====================================================================
def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Bring docs/patchdiffs.js and docs/opcodes.js onto a new game patch.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Run it when the diff lands; run it again with --build once you have a recording.",
    )
    ap.add_argument("--build", type=int, help="game build number of the new patch (int32 at 0x10 of a .dat)")
    ap.add_argument("--from-replay", help="read the build number out of a recording made on the new patch")
    ap.add_argument("--to", help="stop at this patch instead of the newest diff published")
    ap.add_argument("--diffs", help="a local opcodediff diffs/ to mirror from instead of downloading")
    ap.add_argument("--check", action="store_true", help="report what would change, write nothing")
    ap.add_argument("--no-old", action="store_true", help="leave docs/old/opcodes.js alone")
    ap.add_argument("--verify-names", action="store_true", default=True,
                    help="cross-check derived names against FFXIVOpcodes (default)")
    ap.add_argument("--no-verify-names", dest="verify_names", action="store_false")
    ap.add_argument("--merge-new-names", action="store_true",
                    help="also adopt names FFXIVOpcodes has that the diff could not carry forward")
    args = ap.parse_args(argv)

    def log(msg):
        print(msg)

    def section(title):
        print(f"\n== {title} ==")

    dry = args.check
    if dry:
        print("--check: nothing will be written (the diff mirror is still filled)")

    # ---- build number -------------------------------------------------
    build, hist = args.build, None
    if args.from_replay:
        found, hist = build_from_replay(args.from_replay, log)
        if build and build != found:
            raise Fatal(f"--build {build} disagrees with {args.from_replay} (build {found})")
        build = found

    # ---- 1. diffs -----------------------------------------------------
    section("diffs")
    chain = list(build_patchdiffs.VERSION_CHAIN)
    log(f"mirror: {os.path.relpath(MIRROR_DIR, REPO_ROOT)}")
    local = args.diffs
    if local and not os.path.isdir(local):
        raise Fatal(f"--diffs {local} is not a directory")
    if not local:
        sibling = os.path.join(os.path.dirname(REPO_ROOT), "opcodediff", "diffs")
        local = sibling if os.path.isdir(sibling) else None
    available = sync_diffs(MIRROR_DIR, chain[0], local, log)

    # ---- 2. chain -----------------------------------------------------
    section("chain")
    newest = max(available, key=version_key)
    ceiling = args.to or newest
    if args.to and args.to not in available:
        raise Fatal(f"--to {args.to}: no {args.to}.diff.json published yet")
    additions = [v for v in available
                 if version_key(chain[-1]) < version_key(v) <= version_key(ceiling)]

    # An expansion replaces the engine, so a recording carried across that
    # boundary is not a recording of the new client no matter how well the
    # opcodes line up -- which is why the chain starts at 7.00 rather than at the
    # oldest diff opcodediff ships. Stop at the boundary unless told otherwise.
    major = version_key(chain[-1])[0]
    crossing = [v for v in additions if version_key(v)[0] != major]
    if crossing and not args.to:
        log(f"stopping short of {crossing[0]}: that is a new expansion, and the chain "
            f"deliberately does not cross one (see VERSION_CHAIN in build_patchdiffs.py).")
        log(f"    pass --to {crossing[-1]} to chain across it anyway.")
        additions = [v for v in additions if v not in crossing]

    if not additions:
        log(f"chain is already current at {chain[-1]} (newest diff published: {newest})")
    else:
        log(f"new patch(es): {', '.join(additions)}")
        previous = chain[-1]
        for version in additions:
            log(f"  {version}: {hop_alignment(MIRROR_DIR, previous, version)}")
            previous = version
        chain = chain + additions
        if not dry:
            extend_chain_literal(BUILD_PATCHDIFFS_PY, "VERSION_CHAIN", additions)
            extend_chain_literal(BUMP_REPLAY_PY, "FALLBACK_CHAIN", additions)
            log("VERSION_CHAIN and FALLBACK_CHAIN extended")

    tail = chain[-1]
    if not dry:
        regenerate_patchdiffs(chain, MIRROR_DIR, log)

    # ---- 3. names -----------------------------------------------------
    section("names")
    js = read_text(OPCODES_JS)
    tables = read_tables(js)
    # Names are only ever carried forward from the newest table already pasted in,
    # so a re-run finds nothing to do and the hand-corrections in that table keep
    # propagating instead of being overwritten by a fresh derivation.
    source_patch = max((v for v in tables if tables[v]), key=version_key)
    missing = [v for v in chain if version_key(v) > version_key(source_patch)]
    new_tables: dict[str, dict[str, int]] = {}
    if not missing:
        log(f"OPCODE_TABLES already has an entry for {tail}")
    else:
        table = tables[source_patch]
        log(f"carrying {len(table)} names forward from {source_patch}")
        for version in missing:
            table, lost = carry_names(table, load_hop(MIRROR_DIR, version))
            new_tables[version] = table
            log(f"  {version}: {len(table)} names ({len(lost)} lost)")
            for name, opcode, why in lost:
                mark = "  !! " if name in CRITICAL_NAMES else "     "
                log(f"{mark}{name} ({opcode}) {why}")
            if any(name in CRITICAL_NAMES for name, _, _ in lost):
                log("  !! the workbench looks those up by name -- fix them by hand before shipping")
            dupes = collisions(table)
            if dupes:
                log(f"  !! {len(dupes)} opcode(s) carry two names; transpose refuses tables like this:")
                for opcode, names in list(dupes.items())[:4]:
                    log(f"     {opcode} = {' + '.join(names)}")

        if args.verify_names:
            published = fetch_published_names(tail, log)
            if published:
                only_theirs = cross_check(new_tables[tail], published, log)
                if only_theirs and args.merge_new_names:
                    taken = set(new_tables[tail].values())
                    added = {n: o for n, o in only_theirs.items() if o not in taken}
                    new_tables[tail].update(added)
                    log(f"  merged {len(added)} published name(s) the diff could not carry forward")

        if not dry:
            for version in missing:
                js = insert_table(js, version, new_tables[version])
            log(f"docs/opcodes.js: added OPCODE_TABLES entries for {', '.join(missing)}")

    # ---- 4. build number ----------------------------------------------
    section("build")
    if build is None:
        log(f"no build number given, so LATEST_PATCH stays at {read_latest(js)}.")
        log("Record a replay on the new client and re-run:")
        log('    python tools/update_patch.py --build <number>   (or --from-replay "that.dat")')
        log("Bumping LATEST_GAME_BUILD without it would stamp exports with the old build,")
        log("and the client refuses to load a replay whose build does not match.")
    else:
        log(f"build {build} -> {tail}")
        if not dry:
            js = set_build_to_patch(js, build, tail)
            js = set_latest(js, tail, build)
            update_builds_json(build, tail, log)

    if not dry:
        write_text(OPCODES_JS, js)
        log("docs/opcodes.js written")
        if not args.no_old and os.path.isfile(OLD_OPCODES_JS):
            update_old_opcodes(new_tables, tail, build, log)

    # ---- 5. confirmation ----------------------------------------------
    if hist:
        section("verify")
        confirm_with_replay(hist, chain, MIRROR_DIR, tail, log)

    section("next")
    if dry:
        log("re-run without --check to apply.")
        return 0
    log("PartyList and PartyPortraitInfo have been wrong in the published list before;")
    log(f"confirm them against a real {tail} recording:")
    log('    python tools/find_partylist_opcode.py "some 7.x recording.dat"')
    log("Then check the site loads a recording and reports the right patch.")
    return 0


def read_latest(js: str) -> str:
    m = re.search(r"^\s*(?:let|const)\s+LATEST_PATCH\s*=\s*\"([^\"]*)\"", js, re.M)
    return m.group(1) if m else "?"


def update_builds_json(build: int, patch: str, log) -> None:
    with open(BUILDS_JSON, "r", encoding="utf-8") as f:
        data = json.load(f)
    builds = data.get("builds", {})
    if builds.get(str(build)) == patch:
        log("tools/replay_builds.json already has that build")
        return
    builds[str(build)] = patch
    data["builds"] = {k: builds[k] for k in sorted(builds, key=int)}
    write_text(BUILDS_JSON, json.dumps(data, indent=2, ensure_ascii=False) + "\n")
    log(f"tools/replay_builds.json: +{build} -> {patch}")


def update_old_opcodes(new_tables: dict[str, dict[str, int]], patch: str, build: int | None, log) -> None:
    """docs/old/ is the frozen name-only build of the tool; it transposes by IPC
    name alone, so it needs the same table to keep working on a new patch."""
    text = read_text(OLD_OPCODES_JS)
    existing = read_tables(text)
    for version, table in new_tables.items():
        if version not in existing:
            text = insert_table(text, version, table)
    if build is not None:
        text = set_build_to_patch(text, build, patch)
        text = set_latest(text, patch, build)
    write_text(OLD_OPCODES_JS, text)
    log("docs/old/opcodes.js updated")


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fatal as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)
