# Replay Workbench — desktop editor

A C# port of the browser tool's **Editor** tab (`docs/index.html`): load a duty
recording, read its header, pick a pull off the timeline, and export that pull as
a standalone `.dat`.

```
programs/
  Replay.Workbench.Core/   the format: parse, split, transpose, anonymize, strip
  Replay.Workbench.App/    WinForms GUI over the above
```

## Why this shape

The core library has no UI references and no `System.Windows.Forms` dependency —
it is just the `.dat` format and the opcode chain. The GUI is a thin shell that
reads options off checkboxes and calls into it. If the playback map viewer ever
gets a desktop version (ImGui, Avalonia, anything), none of the format work has
to be redone.

WinForms was picked over ImGui because the editor is a data panel: a readout
grid, two tables, editable name fields, checkboxes, one custom-drawn timeline,
and a save dialog. WinForms does all of that with zero NuGet dependencies and a
real native save dialog — the thing the web version has to fight the browser
for. ImGui would need a render backend plus native DLLs and has no file dialogs;
its advantage (cheap 60fps redraw) only matters for playback, which isn't here.

## Build and run

```bash
dotnet build programs/Replay.Workbench.sln
```

```bash
dotnet run --project programs/Replay.Workbench.App
```

The exe takes an optional `.dat` path, so it can be registered as the "open with"
handler for recordings:

```bash
dotnet run --project programs/Replay.Workbench.App -- "testfiles/sample files/some recording.dat"
```

A single-file build with no framework install needed on the target machine:

```bash
dotnet publish programs/Replay.Workbench.App -c Release -r win-x64 --self-contained
```

## Opcode data

`Replay.Workbench.Core/Data/*.json` is **generated** from `docs/opcodes.js`,
`docs/patchdiffs.js` and `docs/afgear.js` — those stay the single source of
truth — and embedded into the library as resources. After any tool rewrites
those files (`tools/update_patch.py` in particular), regenerate:

```bash
python tools/export_core_data.py
```

Skip that and the desktop build keeps shipping the previous patch's tables while
the web tool moves on.

## Character editor

Each player row has a cogwheel that opens a per-character appearance editor —
the desktop-only feature the browser tool has no equivalent of. It edits the
26-byte customize block (race, clan, gender, face, hairstyle, every colour and
feature slider), all ten gear slots with both dye channels, both weapons,
facewear, and the hide-headgear / hide-weapon toggles. `Copy look` / `Paste look`
move a customize block between characters through the clipboard.

Characters are matched between the two packets by the 8-byte value at
PlayerSpawn +0, which the dialog shows as `key`. It is deliberately *not* called
a content id: measured across the sample recordings it is unique per character
within a file (8 of 8 distinct, every file), but the eight players of one
recording share six of its eight bytes, and the same character carries a
different value in a different recording. So it is a per-recording handle, good
for joining packets and useless for identifying anyone across files.

Anonymize scrambles it anyway, alongside the object IDs. It carries no identity
across files, but anyone holding the unedited original could otherwise line the
two up on it and undo the renaming. All 64 bits are replaced: the field has no
shape worth preserving — two recordings share nothing in it, and neither half
ever appears in the header or chapter area, so nothing cross-references it. Every
occurrence is rewritten together, so the file stays internally consistent.

Three things are worth knowing before using it:

- **Colours are palette indices, not RGB.** Skin, hair, eye and lip colours are
  indices into tables that live in the game's own data files, which this tool
  cannot read. They are editable as numbers, and the dialog says so rather than
  faking a swatch.
- **Gear is stored twice, differently.** The in-arena character reads
  model/variant/dye out of PlayerSpawn; the party-portrait list reads *item ids*
  out of its own packet. There is no model-to-item map without the game's data,
  so the dialog exposes both and leaves them for you to set. Characters with no
  portrait block in the recording have that column disabled.

Only fields you actually change are written, per byte. That matters because a
recording can hold a portrait block that disagrees with its own spawn packet
(grafted test files do), and writing a whole appearance back would silently
overwrite the half you never touched.

`Tools ▸ Register opcode table…` is the desktop equivalent of the browser tool's
dev menu: paste a build number and either a plain `{name: opcode}` map or a full
FFXIVOpcodes `opcodes.json`, and it becomes the latest patch for the life of the
process. Nothing is persisted.

## What is and isn't ported

Ported, and verified byte-for-byte against the browser tool across 32 recordings
(every exported pull's SHA-256 matches, in every combination of strip and
transpose; the anonymize path matches on everything except the deliberately
random object IDs):

- header readout, pull detection, combat timing, waymark and countdown handling
- player-name scan and length-preserving rename
- single-pull export, including the stale instance-load duplicate ("ghost") drop
- opcode transpose to the latest patch, via the diff chain or by IPC name
- player anonymization (race swap, AF gear, facewear, object-ID scramble)
- PartyPortraitInfo stripping
- patch detection from the file's own opcodes, with the hand-pick override

Beyond the browser tool: the per-character appearance editor above. Its codec is
covered by 2274 assertions over 25 recordings — writing a character's own values
back moves zero bytes, every field round-trips through a write/re-read, editing
one character leaves everyone else byte-identical, and a one-byte edit writes
exactly one byte per packet.

Not ported: the **Playback** map viewer (`docs/timeline.js`) and the standalone
**Opcode Inspector** page. Both are separate tools rather than part of the
editor; the core library already exposes everything either would need.
