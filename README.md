<div align="center">

<img alt="StutterBegone" src="stutter-begone.png" width="300px" />

Fixes stuttering when sweeping your mouse over shelves and other items in TCG Card Shop Simulator.

</div>

## Why

The game renders **all** full-screen UI under one shared `ScreenSpaceOverlay` canvas (`Canvas`)
holding ~144,000 `CanvasRenderer`s (only ~50–140 active at a time). Unity rebuilds a canvas
whenever *anything* in it changes, and two things change it constantly:

- **Tooltips** — every hover adds/removes tooltip elements, rebuilding the canvas ~100×/sec.
- **Big screens** — opening one (e.g. Price Check, ~48k renderers) rebuilds the whole canvas.

This plugin reparents **each** top-level screen onto its own small canvas, so a change only
rebuilds that screen. A tooltip transition then rebuilds ~20 elements instead of ~144k — the
hover stutter is gone, and tooltips stay fully visible.

## How it works

For each top-level screen on the shared canvas (in original order), the plugin:

1. Creates a new root canvas `StutterBegone_<screen>` and moves the screen onto it.
2. Sets a `sortingOrder` that preserves the **exact original layering**.
3. Copies the `CanvasScaler` and adds a `GraphicRaycaster` (registered with the game's
   `RaycasterManager`) so it renders and clicks the same as before.

It triggers on the shop scene load and is **idempotent** — re-loading the shop (new day, loading
a save) is a safe no-op for screens already split.

## Config

`BepInEx/config/dr.eldon.stutter.begone.cfg`

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `Verbose` | `false` | Log per-screen reparenting, the canvas census, and rescan details. |
| `ExcludeNames` | *(empty)* | Comma-separated screens to leave on the shared canvas; add a name here if one breaks. |
| `ApplyDelayFrames` | `10` | Frames to wait after the shop loads before splitting. |
| `BaseSortingOrder` | `100` | Base `sortingOrder`; each screen gets this plus a stable per-screen index. |
| `RescanCanvasesKey` | `None` | Key to press to rescan for mod-created runtime canvases and shift them above the screens. |

**Runtime overlays.** Some mods create new canvases at runtime (e.g. `Binder Overhaul`'s
filter/sort modal); these land *below* the reparented screens and get hidden. Set
`RescanCanvasesKey` and press it to shift them back on top.

## If a screen breaks

Some mods (EPL, ShopOS) find screens **by path** (`GameObject.Find("Canvas/…")`) or as a direct
child of the shared canvas; moving a screen can break such lookups. If that happens, add the
screen's **exact name** (see the `REPARENT '…'` log lines) to `ExcludeNames` and restart. Usual
suspects: `RestockItemScreen_Grp`, `RestockItemAddToCartScreen`, `RestockItemCheckoutScreen`.

## Uninstall

Delete `BepInEx/plugins/StutterBegone/`. No game or mod files are modified.

## Building

```
dotnet build -c Release -p:GameDir="C:/path/to/TCG Card Shop Simulator" -o out
```
Deploy `out/StutterBegone.dll` to `BepInEx/plugins/StutterBegone/`.
