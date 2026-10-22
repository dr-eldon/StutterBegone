# Changelog

User-facing changes only. For the full config reference, see [README](README.md).

## v1.3.0

- **Breaking:** Renamed the plugin GUID prefix from `hecrj` to `dr.eldon`
  (plugin GUID is now `dr.eldon.stutter.begone`). On next launch the config file
  will be `BepInEx/config/dr.eldon.stutter.begone.cfg` — a fresh file with
  defaults, so any previous settings (in `hecrj.stutter.begone.cfg`) won't carry
  over automatically.
- **Added:** `Verbose` option (default off). When on, logs detailed debug output
  (per-screen reparenting, canvas census, build progress, rescan details); when off,
  only errors and warnings are logged.
- **Changed:** The split now triggers on the shop scene load (`SceneManager.sceneLoaded`,
  matching the shop scene by its name `Start`; the menu/boot scene is skipped) instead of
  waiting for a content-loading signal and a canvas-stability window. This is deterministic and
  consistent, and it re-runs whenever the shop scene (re)loads (a new day, loading a save). The
  split is now idempotent, so re-running is a safe no-op on already-split screens.
- **Changed:** No dependency on EPL at all — the plugin neither links EPL's API nor declares a
  `[BepInDependency]`. The trigger is scene-based, so the mod works with or without EPL.
- **Changed:** The shared canvas is now located by name (including inactive canvases) with a
  most-top-level-children fallback, and is cached once found. More robust when the canvas is
  small (e.g. with Modded Shop Performance Fix) or momentarily inactive.
- **Removed:** `GiantCanvasName`, `ProbeInterval`, `SettleFrames`, `MaxWaitFrames`,
  `MinTotalCanvasRenderers`, and `UseEplSignal` config options — the scene-triggered split no
  longer needs the content-wait heuristics they controlled, or a configurable canvas name.

## v1.2.0

- **Fixed:** The plugin no longer gives up waiting for content loading after a fixed time.
  It now waits as long as needed, so it won't split the canvas before it's fully built
  (relevant on very long loading screens with a large mod set).

## v1.1.0

- **Added** `RescanCanvasesKey` (no default). Set a key and press it to shift new runtime
  canvases (e.g. a mod's overlay/modal such as Binder Overhaul's filter/sort modal) above the
  reparented screens, fixing z-order conflicts where a mod's UI appeared behind a screen.
- **Removed** the `Mode` setting (`Reparent`/`Nested`/`Off`). The plugin now always reparents
  screens (the previous default behavior).

## v1.0.0

- Initial release. Fixes the hover stutter by reparenting every top-level UI screen onto its
  own small canvas, so a change only rebuilds that screen's canvas instead of the giant shared
  one (~144k CanvasRenderers). Tooltips stay fully visible.
- Config options to enable/disable, exclude specific screens, and tune timing.
