# Guide screenshots

PNG crops shown inside the in-app guides. A guide block references one with a
whole-line directive:

    ![Short caption shown under the image](file-name.png)

`GuideWindow.MakeImage` loads `pack://…/K2.Core;component/Assets/Guides/<file-name.png>`.
The `*.png` glob in `K2.Core.csproj` picks up anything dropped here — no csproj
edit per file. A missing file degrades to just the caption text, so a guide is
never broken by a not-yet-captured image.

## Rules

- **Crop to the relevant part**, not the whole window: the sidebar section +
  its controls, a single dialog panel, the device graphic area with the keys
  in question, etc.
- Capture at 100 % display scaling if possible; target width ≤ 1080 px (the
  popup shows them at ≤ 540 px, `DownOnly`, so a 2× crop stays crisp).
- Dark theme (K2's default). Trim window shadows / desktop.
- Keep names lowercase, `-` between words, `:` in a guide key becomes `-`.

Captured with `tools/_guidecap.ps1` against a non-elevated K2 (see that file's
header). The DisplayPad crops had the machine's personal app icons painted over
with generic example tiles (browser / media) — see `scratchpad/overlay_icons.ps1`
in the session that made them.

## Naming ↔ guide key   (✓ = present & wired)

| file | guide block | what to show |
|---|---|---|
| ✓ `everest-appearance.png`    | `everest:appearance`, `highlights` | Appearance sidebar section + keycap/legend/frame controls |
| ✓ `macropad-appearance.png`   | `macropad:appearance` | same, MacroPad (rotated layout) |
| ✓ `everest-displaykeys.png`   | `everest:keybinding`, `highlights` | dock + crown + 4 numpad screen keys + the "Display Key 1-4" list |
| ✓ `dp-dedicated.png`          | `highlights:displaypad` | the "Dedicated profiles" list under the normal profiles |
| ✓ `dp-profile-exe.png`        | `profiles`, `highlights:displaypad` | Configure-profile dialog: linked program + focus-only + restore-on-close |
| ✓ `dp-rotation-before.png`    | `displaypad:settings`, `highlights:displaypad` | the pad grid at Horizontal (0°) |
| ✓ `dp-rotation-after.png`     | same | the same grid at Vertical (90°), icons re-rotated |
| — `ev60-appearance.png`       | `everest60:appearance` | same as Everest — needs an Everest 60 connected |
| — `keymap-catalog.png`        | `highlights` | action-picker category grid (level 1) |
| — `dp-emoji-browser.png`      | `picker:act:dp_emojibrowser`, `highlights:displaypad` | emoji browser category screen |
| — `clock-faces.png`           | `picker:sub:dp_clock` | the clock sub-action grid |
| — `sysmon-grid.png`           | `picker:sub:dp_sysmon` | the PC-monitor sub-action grid incl. "Sensor selection" |

Add a `![caption](file.png)` line to the block when a new image lands.
