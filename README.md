# Replay Workbench

An editor and splitter for **FINAL FANTASY XIV duty recordings** (`.dat` replay files).

Open a recording, see what's in it, pick out a single pull, clean up or anonymize
the players, and save it as its own replay that the game will load.

**[Open Replay Workbench in your browser →](https://cherryxiv.github.io/Replay.Workbench/)**

It runs entirely in your browser. Recordings are never uploaded, and nothing is
kept after you close or refresh the tab.

---

## Features

- **Read the recording.** Shows the header (recording date, game build,
  recorder, party jobs, length), every pull on a timeline, pull length, time in combat, and
  respawns.
- **Export one pull.** Saves a single pull as a standalone `.dat`. Waymarks placed
  earlier are carried in, and the countdown can be kept as a selectable chapter.
- **Rename players.** Names are rewritten everywhere they appear in the file.
- **Anonymize players.** One checkbox replaces names, object IDs, race, gear,
  weapons, titles, worlds, online status and status icons, so no one in the
  recording can be identified.
- **Strip party portraits.** Removes the PartyPortraitInfo packets entirely.
- **Transpose to the current patch.** Remaps an older recording's opcodes so the
  current client can play it back. Covers every Dawntrail patch from 7.00 onward.
- **Opcode inspector.** A separate read-only page for browsing a recording packet
  by packet, with IPC names.

## Using it

1. Open the [web app](https://cherryxiv.github.io/Replay.Workbench/).
2. Drop a recording onto the page. The game saves them in:
   ```
   Documents\My Games\FINAL FANTASY XIV - A Realm Reborn\replay
   ```
3. Click a pull on the timeline or in the table, choose your export options, and
   click **Export selected pull**.
4. Put the exported file back in the `replay` folder to watch it in game.

## Desktop app (Windows)

[`programs/`](programs/) has a C# / WinForms version of the same editor. Its
output matches the browser version byte for byte, and it adds a
**per-character appearance editor**: race, clan, face, hair, colours, all ten gear
slots with dyes, weapons, and facewear.

Building it needs the [.NET SDK](https://dotnet.microsoft.com/download):

```bash
dotnet run --project programs/Replay.Workbench.App
```

See [`programs/README.md`](programs/README.md) for build options and details.

## Patch days

A recording only loads on the client build it was recorded on, so every game
patch needs new opcode data. [`tools/`](tools/) has the scripts that do this, and
[`tools/Replay.Workbench.Updater`](tools/Replay.Workbench.Updater/) wraps the whole
process in one app. To run the scripts directly:

```bash
python tools/update_patch.py --build <game build number>
```

## Repository layout

| Path | What it is |
| --- | --- |
| [`docs/`](docs/) | The web app (served by GitHub Pages). `docs/old/` is the previous version. |
| [`programs/`](programs/) | The Windows desktop editor and its core library. |
| [`tools/`](tools/) | Patch-day updater and opcode data scripts. |

## Reporting problems

If a recording doesn't parse, an export won't load in game, or a patch broke
something, please [open an issue](https://github.com/CherryXIV/Replay.Workbench/issues/new).
Say which patch the recording was made on. Please don't attach recordings that
show other players' names unless they're anonymized first.

## Credits

- Opcode names: [karashiiro/FFXIVOpcodes](https://github.com/karashiiro/FFXIVOpcodes)
- Per-patch opcode diffs: [xivdev/opcodediff](https://github.com/xivdev/opcodediff)

FINAL FANTASY XIV is a registered trademark of Square Enix Holdings Co., Ltd.
This project is not affiliated with or endorsed by Square Enix.
