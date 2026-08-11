#!/usr/bin/env python3
"""
Bump an FFXIVREPLAY .dat from the patch it was recorded on to a newer one, one
patch at a time, using the diff files from the opcodediff repo.

The workbench's own "transpose opcodes" maps packets by IPC *name*, so it only
works between patches that have a table pasted into docs/opcodes.js (currently
7.51 and later). opcodediff instead records, for every patch, which opcode
number became which - no names needed. Chaining those diffs walks a recording
forward across every Dawntrail patch (7.00 onward; see FALLBACK_CHAIN).

    <patch>.diff.json maps the *previous* patch to <patch>.

Both routes were cross-checked: composing 7.51 -> 7.51h -> 7.51h2 -> 7.55 ->
7.55h through the diffs agrees with the name-based remap on all 171 opcodes the
two tables share.

Only the u16 opcode in each segment header is touched, so the file's size,
segment offsets and chapter table all stay valid. The game build stamp at 0x10
is rewritten too: a replay only loads if its build matches the running client.

Usage:
    python tools/bump_replay.py "My Pull.dat"
    python tools/bump_replay.py "My Pull.dat" -o out.dat --to 7.55
    python tools/bump_replay.py "My Pull.dat" --from 7.30 --strict
    python tools/bump_replay.py --info "My Pull.dat"
    python tools/bump_replay.py --list
"""

from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys

# ---- FFXIVReplay .dat layout (mirrors docs/app.js) ----
MAGIC = b"FFXIVREPLAY\0"
HEADER_SIZE = 0x68
CHAPTER_ARRAY = 0x4 + 0xC * 64
DATA_START = HEADER_SIZE + CHAPTER_ARRAY  # 0x36C
SEG_HEADER = 12  # u16 opcode; u16 dataLength; u32 ms; u32 objectID
OFF_BUILD = 0x10
OFF_REPLAY_LEN = 0x48

# Segments at or above this are replay control markers (0xf001/0xf002), not IPC.
NON_IPC_OPCODE = 0xF000

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDS_FILE = os.path.join(REPO_ROOT, "tools", "replay_builds.json")

# Patch order, oldest first; the first entry is the base version, which has no
# diff of its own. It starts at 7.00 on purpose: Dawntrail replaced the engine,
# so carrying a 6.x recording across that boundary is meaningless no matter how
# well the opcodes line up.
#
# docs/patchdiffs.js is the single source for this — it ships the chain it was
# generated with, and the site reads the same file. The list below is only a
# fallback for running against raw opcodediff files without a generated copy.
FALLBACK_CHAIN = [
    "7.00", "7.00h", "7.01", "7.05", "7.05h", "7.05h2",
    "7.10", "7.11", "7.15", "7.16", "7.16h", "7.18", "7.18h",
    "7.20", "7.20h", "7.21", "7.25", "7.25h", "7.25h2", "7.25h3",
    "7.30", "7.30h", "7.31", "7.31h", "7.35", "7.35h", "7.38",
    "7.40", "7.40h", "7.40h2", "7.41", "7.41h", "7.45", "7.45h", "7.45h2",
    "7.50", "7.50h", "7.51", "7.51h", "7.51h2", "7.55", "7.55h",
]


class Fatal(Exception):
    pass


# =====================================================================
# Diffs
# =====================================================================
def find_diffs_dir(explicit: str | None) -> str:
    """Locate opcodediff's diffs/ - flag, then env, then the usual checkouts."""
    candidates = []
    if explicit:
        candidates.append(explicit)
    if os.environ.get("OPCODEDIFF_DIFFS"):
        candidates.append(os.environ["OPCODEDIFF_DIFFS"])
    candidates += [
        os.path.join(REPO_ROOT, "tools", "diffs"),
        os.path.join(os.path.dirname(REPO_ROOT), "opcodediff", "diffs"),
    ]
    for c in candidates:
        if os.path.isdir(c):
            return c
    raise Fatal(
        "Could not find opcodediff's diffs/ directory. Pass --diffs /path/to/opcodediff/diffs "
        "or set OPCODEDIFF_DIFFS.\nLooked in:\n  " + "\n  ".join(candidates)
    )


def _hop(version, mapping, ambiguous, removed):
    # Two sources landing on one target would collapse two packet types onto a
    # single opcode. Demote both rather than pick one; see build_patchdiffs.py.
    dupes: dict[int, list[int]] = {}
    for old, new in mapping.items():
        dupes.setdefault(new, []).append(old)
    for olds_on_target in [o for o in dupes.values() if len(o) > 1]:
        for old in olds_on_target:
            del mapping[old]
            ambiguous.add(old)
    return {
        "version": version,
        "map": mapping,
        "ambiguous": ambiguous,
        "removed": removed,
        "known": set(mapping) | ambiguous | removed,  # every opcode the previous patch used
    }


def load_hop(diffs_dir: str, version: str):
    """Read <version>.diff.json into a previous-patch -> this-patch opcode map.

    An entry pairs one old opcode with one new one when the diff resolved it.
    Everything else is attrition worth reporting rather than guessing at:
      * n:n groups (n > 1) are candidates the matcher could not tell apart,
      * an entry with no "new" is an opcode that went away,
      * an entry with no "old" is one that appeared this patch.
    The 6.3 diff spells the unresolved case as "candidates"/"unknown" keys; both
    shapes fall out of the same len() checks.
    """
    path = os.path.join(diffs_dir, f"{version}.diff.json")
    if not os.path.isfile(path):
        raise Fatal(f"No diff for {version} (expected {path})")
    with open(path, "r", encoding="utf-8") as f:
        entries = json.load(f)

    mapping: dict[int, int] = {}
    ambiguous: set[int] = set()
    removed: set[int] = set()
    for e in entries:
        olds = [int(x, 16) for x in (e.get("old") or [])]
        news = [int(x, 16) for x in (e.get("new") or [])]
        if len(olds) == 1 and len(news) == 1:
            mapping[olds[0]] = news[0]
        elif not olds:
            continue  # a brand new opcode; nothing on the old side to carry forward
        elif not news:
            removed.update(olds)
        else:
            ambiguous.update(olds)
    return _hop(version, mapping, ambiguous, removed)


# ---- docs/patchdiffs.js: the same hops, baked and committed ----
PATCHDIFFS_JS = os.path.join(REPO_ROOT, "docs", "patchdiffs.js")
PACK_ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_"
PACK_INDEX = {c: i for i, c in enumerate(PACK_ALPHABET)}


def unpack(s: str) -> list[int]:
    return [PACK_INDEX[s[i]] * 64 + PACK_INDEX[s[i + 1]] for i in range(0, len(s), 2)]


def load_baked(path: str = PATCHDIFFS_JS):
    """Load the patch chain and hops out of docs/patchdiffs.js.

    That file is generated from the same diffs by tools/build_patchdiffs.py and
    is what the web workbench transposes with, so reading it here keeps the CLI
    and the site provably on the same data instead of two copies that drift —
    including the chain itself, so trimming the chain in one place trims it
    everywhere. Returns (chain, hops), or (None, None) if the file isn't there.
    """
    if not os.path.isfile(path):
        return None, None
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()

    def grab(name):
        start = text.index(name)
        start = text.index("=", start) + 1
        # The file is generated, so the first balanced bracket run is the literal.
        open_ch = text[start:].lstrip()[0]
        close_ch = {"[": "]", "{": "}"}[open_ch]
        i = text.index(open_ch, start)
        depth = 0
        for j in range(i, len(text)):
            if text[j] == open_ch:
                depth += 1
            elif text[j] == close_ch:
                depth -= 1
                if depth == 0:
                    return text[i : j + 1]
        raise Fatal(f"{path}: {name} literal is unterminated")

    trailing_comma = re.compile(r",(\s*[}\]])")
    chain = json.loads(trailing_comma.sub(r"\1", grab("const PATCH_CHAIN")))
    # Object keys are bare identifiers in the generated file; quote them for json.
    raw = trailing_comma.sub(r"\1", grab("const PATCH_DIFFS"))
    for key in ("o", "n", "a", "r"):
        raw = raw.replace(f"{key}:\"", f"\"{key}\":\"")
    diffs = json.loads(raw)

    hops = {}
    for version, packed in diffs.items():
        if version not in chain:
            continue  # a hop for a patch the chain no longer covers
        olds, news = unpack(packed["o"]), unpack(packed["n"])
        hops[version] = _hop(version, dict(zip(olds, news)), set(unpack(packed.get("a", ""))),
                             set(unpack(packed.get("r", ""))))
    missing = [v for v in chain[1:] if v not in hops]
    if missing:
        raise Fatal(
            f"{path} lists {', '.join(missing[:5])} in PATCH_CHAIN but has no diff for "
            f"{'them' if len(missing) > 1 else 'it'}. Re-run tools/build_patchdiffs.py."
        )
    return chain, hops


# The chain the generated data actually carries wins; the constant is the fallback
# for a checkout without docs/patchdiffs.js (raw --diffs runs).
try:
    VERSION_CHAIN = load_baked()[0] or FALLBACK_CHAIN
except Exception:
    VERSION_CHAIN = FALLBACK_CHAIN


def patch_universe(hops: dict, patch: str) -> set:
    """Every opcode a patch is known to use: the old side of the hop that leaves it."""
    i = VERSION_CHAIN.index(patch)
    if i + 1 < len(VERSION_CHAIN):
        return hops[VERSION_CHAIN[i + 1]]["known"]
    return set(hops[patch]["map"].values())


def detect_patch(hops: dict, hist: dict, dst: str):
    """Which patch was this recording made on? Ask the file, not the build number.

    Every patch reshuffles the whole IPC vtable, so a recording's opcodes only fit
    the patch it was actually made on. Score each candidate by the share of the
    file's packets its vtable accounts for AND can carry all the way to `dst`; the
    right patch lands on 100% while its neighbours sit well below.

    Worth the trouble because the alternative fails quietly: guess the patch one
    hotfix off and every packet still gets remapped, just onto the wrong type.
    Returns (best, runner_up) as (patch, packet_fraction, kind_fraction) tuples.
    """
    ipc = {o: c for o, c in hist.items() if o < NON_IPC_OPCODE}
    total = sum(ipc.values())
    if not total:
        return None, None
    scores = []
    for src in VERSION_CHAIN[: VERSION_CHAIN.index(dst) + 1]:
        live = {o: o for o in ipc}
        for v in VERSION_CHAIN[VERSION_CHAIN.index(src) + 1 : VERSION_CHAIN.index(dst) + 1]:
            step = hops[v]["map"]
            live = {orig: step[cur] for orig, cur in live.items() if cur in step}
        if src == dst:
            uni = patch_universe(hops, src)
            live = {o: o for o in ipc if o in uni}
        scores.append((sum(ipc[o] for o in live) / total, len(live) / len(ipc), src))
    scores.sort(reverse=True)
    best = scores[0]
    runner = scores[1] if len(scores) > 1 else None
    return (best[2], best[0], best[1]), (runner[2], runner[0], runner[1]) if runner else None


def chain_between(src: str, dst: str) -> list[str]:
    for v in (src, dst):
        if v not in VERSION_CHAIN:
            raise Fatal(f"Unknown patch {v!r}. --list shows the chain.")
    i, j = VERSION_CHAIN.index(src), VERSION_CHAIN.index(dst)
    if j < i:
        raise Fatal(f"{dst} is older than {src}; this only bumps forward.")
    return VERSION_CHAIN[i + 1 : j + 1]


# =====================================================================
# Builds
# =====================================================================
def load_builds() -> dict[int, str]:
    if not os.path.isfile(BUILDS_FILE):
        return {}
    with open(BUILDS_FILE, "r", encoding="utf-8") as f:
        data = json.load(f)
    return {int(k): v for k, v in data.get("builds", {}).items()}


def build_for_version(builds: dict[int, str], version: str) -> int | None:
    hits = [b for b, v in builds.items() if v == version]
    return max(hits) if hits else None


# =====================================================================
# Replay file
# =====================================================================
def read_replay(path: str) -> bytearray:
    with open(path, "rb") as f:
        data = bytearray(f.read())
    if data[: len(MAGIC)] != MAGIC:
        raise Fatal(f"{path} is not an FFXIVREPLAY .dat (bad header magic).")
    if len(data) < DATA_START:
        raise Fatal(f"{path} is truncated ({len(data)} bytes).")
    return data


def segment_offsets(data: bytearray, recover: bool = True) -> list[int]:
    """Absolute file offsets of every segment header, walking the data section.

    A recording the game never finalised has 0 at 0x48 even though the packets are
    all there -- it writes the length back on exit, so a crash or a kill leaves the
    field unset. Those files are perfectly recoverable: walk to EOF instead, and
    the caller stamps the real length on the way out.
    """
    replay_len = struct.unpack_from("<i", data, OFF_REPLAY_LEN)[0]
    unfinalised = replay_len <= 0
    if not unfinalised and DATA_START + replay_len > len(data):
        raise Fatal(
            f"Replay length at 0x48 ({replay_len}) runs past the end of the file "
            f"({len(data) - DATA_START} bytes of data available)."
        )
    if unfinalised:
        if not recover:
            raise Fatal("Replay length at 0x48 is 0 (unfinalised recording).")
        replay_len = len(data) - DATA_START

    offsets = []
    off = 0
    while off < replay_len:
        base = DATA_START + off
        if base + SEG_HEADER > len(data):
            break
        length = struct.unpack_from("<H", data, base + 2)[0]
        if base + SEG_HEADER + length > len(data):
            break  # trailing partial segment from an interrupted write
        offsets.append(base)
        off += SEG_HEADER + length
    if not unfinalised and off != replay_len:
        raise Fatal(
            f"Segment walk overran the data section (ended at {off}, expected {replay_len}). "
            "The file is truncated or not a replay this tool understands."
        )
    if unfinalised:
        segment_offsets.recovered = off
    else:
        segment_offsets.recovered = None
    return offsets


def opcode_histogram(data: bytearray, offsets: list[int]) -> dict[int, int]:
    hist: dict[int, int] = {}
    for base in offsets:
        op = struct.unpack_from("<H", data, base)[0]
        hist[op] = hist.get(op, 0) + 1
    return hist


# =====================================================================
# The bump itself
# =====================================================================
def bump_opcodes(hist: dict[int, int], hops: list[dict], verbose: bool):
    """Walk the file's opcodes forward one patch at a time.

    Returns (final map original -> target, lost {original: reason}). Rewriting
    the bytes is one pass at the end; doing it per hop would just be the same
    permutation applied 60 times to an 86 MB file.
    """
    ipc = {op: op for op in hist if op < NON_IPC_OPCODE}
    lost: dict[int, str] = {}

    for hop in hops:
        moved = 0
        for orig in list(ipc):
            cur = ipc[orig]
            if cur in hop["map"]:
                new = hop["map"][cur]
                if new != cur:
                    moved += 1
                ipc[orig] = new
            elif cur in hop["ambiguous"]:
                lost[orig] = f"ambiguous in the {hop['version']} diff"
                del ipc[orig]
            elif cur in hop["removed"]:
                lost[orig] = f"removed in {hop['version']}"
                del ipc[orig]
            else:
                # Not an opcode this patch's vtable knows about. Nothing to map
                # it to, and nothing that says it changed - leave it alone.
                lost[orig] = f"not in the {hop['version']} diff"
                del ipc[orig]
        if verbose:
            print(
                f"  -> {hop['version']}: {moved} of {len(ipc)} live opcodes moved"
                + (f", {len(lost)} lost so far" if lost else "")
            )
    return ipc, lost


def check_collisions(final: dict[int, int], hist: dict[int, int], lost: dict[int, str]):
    """Ways the remapped file could hand the client a packet it will misparse.

    Two source opcodes landing on one target collapses two packet types into
    one; a stale opcode left behind (because it could not be mapped) that some
    *other* packet now moved onto does the same thing. Either one is the
    "3672-byte PartyList arriving on PlayerSpawn's opcode" crash.
    """
    by_target: dict[int, list[int]] = {}
    for orig, new in final.items():
        by_target.setdefault(new, []).append(orig)
    merges = {t: srcs for t, srcs in by_target.items() if len(srcs) > 1}

    used = set(final.values())
    stale = {op: hist[op] for op in lost if op in used}
    return merges, stale


# =====================================================================
# CLI
# =====================================================================
def cmd_list(diffs_dir: str | None):
    print("Patch chain (oldest first; the first entry is the base with no diff):")
    have = set()
    if diffs_dir:
        if os.path.isdir(diffs_dir):
            have = {n[: -len(".diff.json")] for n in os.listdir(diffs_dir) if n.endswith(".diff.json")}
    else:
        _, baked = load_baked()
        if baked:
            have = set(baked)
    for i, v in enumerate(VERSION_CHAIN):
        mark = "" if i == 0 or v in have or not have else "   (diff MISSING)"
        print(f"  {v}{mark}")
    extra = have - set(VERSION_CHAIN)
    if extra:
        print("\nDiffs present but not in the chain (add them to VERSION_CHAIN):")
        for v in sorted(extra):
            print(f"  {v}")


def cmd_info(path: str, builds: dict[int, str]):
    data = read_replay(path)
    build = struct.unpack_from("<i", data, OFF_BUILD)[0]
    offsets = segment_offsets(data)
    hist = opcode_histogram(data, offsets)
    version = builds.get(build)
    print(f"file        : {path}")
    print(f"size        : {len(data):,} bytes")
    print(f"game build  : {build}" + (f"  ({version})" if version else "  (unknown - see tools/replay_builds.json)"))
    print(f"segments    : {len(offsets):,}")
    print(f"distinct ops: {len(hist)} " f"({sum(1 for o in hist if o >= NON_IPC_OPCODE)} control markers)")


def main(argv=None):
    p = argparse.ArgumentParser(
        description="Bump an FFXIVREPLAY .dat forward across patches using opcodediff's diff files.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("input", nargs="?", help="the .dat to convert")
    p.add_argument("-o", "--output", help="output path (default: <input>.<target>.dat)")
    p.add_argument("--from", dest="src", help="source patch (default: from the file's game build)")
    p.add_argument("--to", dest="dst", default=VERSION_CHAIN[-1], help=f"target patch (default: {VERSION_CHAIN[-1]})")
    p.add_argument("--diffs", help="read opcodediff's raw diffs/ instead of docs/patchdiffs.js")
    p.add_argument("--stamp-build", type=int, help="build number to write at 0x10 (default: looked up from --to)")
    p.add_argument("--no-stamp", action="store_true", help="leave the game build stamp alone")
    p.add_argument("--strict", action="store_true", help="refuse to write if anything could collide or was lost")
    p.add_argument("-n", "--dry-run", action="store_true", help="report only, write nothing")
    p.add_argument("-q", "--quiet", action="store_true", help="skip the per-patch trace")
    p.add_argument("--info", action="store_true", help="print what the file is and exit")
    p.add_argument("--list", action="store_true", help="print the patch chain and exit")
    args = p.parse_args(argv)

    builds = load_builds()

    if args.list:
        cmd_list(args.diffs)
        return 0
    if not args.input:
        p.error("an input .dat is required (or use --list)")
    if args.info:
        cmd_info(args.input, builds)
        return 0

    # docs/patchdiffs.js is the committed, generated copy the web workbench also
    # transposes with -- same data, one source. --diffs reads opcodediff's raw
    # files instead, for a patch whose diff exists but hasn't been baked in yet.
    baked = None if args.diffs else load_baked()[1]
    diffs_dir = None if baked else find_diffs_dir(args.diffs)
    if not args.quiet:
        print(f"opcode data: {'docs/patchdiffs.js' if baked else diffs_dir}")
    data = read_replay(args.input)
    build = struct.unpack_from("<i", data, OFF_BUILD)[0]
    offsets = segment_offsets(data)
    recovered = getattr(segment_offsets, "recovered", None)
    if recovered is not None:
        print(f"unfinalised recording: 0x48 was 0, recovered {len(offsets):,} segments "
              f"({recovered:,} bytes); the output will carry the real length")
    hist = opcode_histogram(data, offsets)
    ipc_segs = sum(c for o, c in hist.items() if o < NON_IPC_OPCODE)

    all_hops = baked if baked else {v: load_hop(diffs_dir, v) for v in VERSION_CHAIN[1:]}
    best, runner = detect_patch(all_hops, hist, args.dst)
    # Detection only overrules the build table when it accounts for the file
    # exactly and nothing else comes close.
    # Score on opcode *kinds*, not packet share: one chatty opcode (ActorMove is
    # half the file) pins the packet share near 100% for several patches, while
    # the count of opcodes a patch can account for separates them cleanly.
    confident = bool(best and best[1] >= 0.9999 and best[2] >= 0.9999
                     and (not runner or best[2] - runner[2] > 0.01))
    from_build = builds.get(build)

    if args.src:
        src, how = args.src, "you asked for it"
    elif confident:
        src, how = best[0], f"read from the file's opcodes ({best[1] * 100:.0f}% fit, next best {runner[0] if runner else '-'})"
    elif from_build:
        src, how = from_build, f"from game build {build}"
    else:
        raise Fatal(
            f"Can't tell which patch this is. Build {build} isn't in tools/replay_builds.json and "
            f"the opcodes don't clearly match one patch"
            + (f" (closest: {best[0]} at {best[1] * 100:.1f}%)" if best else "")
            + ". Pass --from <patch>."
        )
    if not args.quiet:
        print(f"source patch {src} ({how})")
    # A wrong build entry does not fail loudly -- every packet still gets remapped,
    # just onto the wrong packet type. Say so rather than let it through silently.
    if confident and from_build and from_build != best[0] and not args.src:
        print(f"WARNING: build {build} is listed as {from_build}, but this file's packets are {best[0]}. "
              f"Using {best[0]}; fix the build table.")

    hops = chain_between(src, args.dst)
    if not hops:
        print(f"{args.input} is already on {args.dst}; nothing to do.")
        return 0

    if not args.quiet:
        print(f"{len(offsets):,} segments, {len(hist)} distinct opcodes")
        print(f"bumping {src} -> {args.dst} ({len(hops)} patches)")

    hop_data = [all_hops.get(v) for v in hops]
    missing = [v for v, h in zip(hops, hop_data) if h is None]
    if missing:
        raise Fatal(f"no opcode data for {', '.join(missing)}; re-run tools/build_patchdiffs.py")
    final, lost = bump_opcodes(hist, hop_data, verbose=not args.quiet)
    merges, stale = check_collisions(final, hist, lost)

    changed_segs = sum(hist[o] for o, n in final.items() if n != o)
    lost_segs = sum(hist[o] for o in lost)
    print(
        f"mapped {len(final)} of {len(final) + len(lost)} IPC opcodes "
        f"({changed_segs:,} of {ipc_segs:,} IPC segments rewritten)"
    )

    problems = []
    if lost:
        print(f"\n{len(lost)} opcode(s) could not be carried forward, covering {lost_segs:,} segments:")
        for op in sorted(lost, key=lambda o: -hist[o]):
            print(f"  0x{op:03x}  {hist[op]:>8,} segments  - {lost[op]}")
        print("  These keep their original opcode. The client will read them as whatever")
        print(f"  packet owns that number in {args.dst}.")
        problems.append(f"{len(lost)} unmapped opcode(s)")
    if stale:
        print(f"\nCOLLISION: {len(stale)} unmapped opcode(s) sit on numbers another packet moved onto:")
        for op, count in sorted(stale.items(), key=lambda kv: -kv[1]):
            print(f"  0x{op:03x}  {count:,} segments")
        print("  Loading this is what crashes the client. Strip those packets first.")
        problems.append(f"{len(stale)} stale-opcode collision(s)")
    if merges:
        print(f"\nCOLLISION: {len(merges)} target opcode(s) have two sources mapped onto them:")
        for target, srcs in list(merges.items())[:5]:
            print(f"  0x{target:03x} <- " + ", ".join(f"0x{s:03x}" for s in srcs))
        problems.append(f"{len(merges)} merged opcode(s)")

    if problems and args.strict:
        raise Fatal("--strict: refusing to write (" + "; ".join(problems) + ")")

    if args.dry_run:
        print("\ndry run - nothing written")
        return 0

    # Rewrite. Size-preserving, so offsets and the chapter table stay valid.
    for base in offsets:
        op = struct.unpack_from("<H", data, base)[0]
        new = final.get(op)
        if new is not None and new != op:
            struct.pack_into("<H", data, base, new)

    if recovered is not None:
        struct.pack_into("<i", data, OFF_REPLAY_LEN, recovered)

    if not args.no_stamp:
        stamp = args.stamp_build or build_for_version(builds, args.dst)
        if stamp is None:
            raise Fatal(
                f"No game build known for {args.dst}, so the file cannot be stamped and the client "
                f"will refuse it. Add it to tools/replay_builds.json, pass --stamp-build, or --no-stamp."
            )
        struct.pack_into("<i", data, OFF_BUILD, stamp)
        print(f"game build {build} -> {stamp}")

    out = args.output
    if not out:
        stem, ext = os.path.splitext(args.input)
        out = f"{stem}.{args.dst}{ext or '.dat'}"
    with open(out, "wb") as f:
        f.write(data)
    print(f"wrote {out}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fatal as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)
