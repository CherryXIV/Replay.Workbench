# Patch updater

One app for the whole patch-day chore: mirror opcodediff, extend the chain,
regenerate `docs/patchdiffs.js`, derive the new IPC name table, bump
`LATEST_PATCH` / `LATEST_GAME_BUILD`, re-export the desktop core's embedded data,
rebuild the editor, and validate the result against a recording.

```
tools/Replay.Workbench.Updater/
  Updater.Core/   the ported update logic (no UI, no editor references)
  Updater.App/    the WinForms front end
```

```bash
dotnet build tools/Replay.Workbench.Updater/Replay.Workbench.Updater.sln
```

```bash
dotnet run --project tools/Replay.Workbench.Updater/Updater.App
```

## How it is used

Fill in what you have and press **Preview**. Nothing is written until you press
**Apply**, and Apply is disabled until a preview has run.

- **Game build number** — the int32 at 0x10 of a `.dat`. Leave it empty the day
  the diff lands; run again with it once you have a recording.
- **…or a recording** — reads the build out of a `.dat` made on the new patch,
  and cross-checks the recording's own opcodes against the patch that was added.
- **Stop at patch** / **Local opcodediff diffs/** — optional.

The two halves of an update usually arrive on different days: opcodediff
publishes the diff within hours, but the build number can only be read out of a
recording made on the new client. Every step is idempotent, so re-running is the
expected workflow, not a recovery move.

## Why it is a port, and how that is kept honest

This is a C# port of `tools/update_patch.py`, which **stays in the tree**. It is
not dead weight: it is the oracle the port is tested against. `scratchpad/e2e`
rewinds two copies of the repo past the newest patch, lets the Python update one
and the C# update the other, and requires all nine touched files to come out
byte-identical — `docs/opcodes.js`, `docs/patchdiffs.js`, `docs/old/opcodes.js`,
both chain literals, `tools/replay_builds.json` and the three embedded JSON
files. It currently passes on all nine.

That test is the whole reason a port was defensible. Without it there is no way
to tell a faithful port from one that silently derives a wrong name table, and a
wrong table does not fail loudly — every packet still gets remapped, just onto
the wrong packet type, and the export crashes the game client.

Two behaviours worth knowing about, both inherited from the Python and both
confirmed to match it:

- Applying an update **normalises** `BUILD_TO_PATCH` and `replay_builds.json`
  formatting, so a hand-edited file comes back slightly reformatted.
- Names are **derived**, not downloaded: the previous patch's table is carried
  forward through the diff, which keeps this repo's hand-corrections instead of
  re-importing a third-party dump that has been wrong before.

## Safety

- **Preview first.** These files routinely carry uncommitted work, so git is not
  a reliable undo here.
- **Backups.** Apply copies all nine files into
  `tools/.update-backups/<timestamp>/` first; **Restore last backup** puts them
  back.
- **The editor must be closed** before a rebuild — it holds its own assemblies
  open, and MSBuild fails with `MSB3027` half way through. The updater checks for
  a running `Replay.Workbench.exe` and refuses up front rather than failing late.
- The updater takes **no project reference** to `Replay.Workbench.Core`, for the
  same reason: it must not hold an assembly it is about to overwrite. Validation
  runs as a child process instead.

## Validation

`programs/Replay.Workbench.Verify` is built by the rebuild step and run as the
last one, so it always tests the data that was just written:

- the latest patch resolves to a name table, with **no opcode carrying two
  packet names** (the failure that crashes clients)
- every packet the workbench looks up by name is still present
- the oldest patch in the chain can still be remapped to the newest
- some build number maps to the latest patch
- for each recording passed: it parses, its patch is confidently detected, a pull
  exports and re-parses, and it transposes forward

It exits non-zero on any failure, so it is equally usable by hand or in CI:

```bash
dotnet run --project programs/Replay.Workbench.Verify -- "some recording.dat"
```

## Known issue

The **Browse…** buttons beside the recording and diffs fields do not render.
Both fields accept a typed or pasted path, so the app is usable, but the buttons
need fixing.
