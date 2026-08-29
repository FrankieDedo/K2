# K2 — Installation & Base Camp options

K2 is an unofficial, full replacement for Mountain's **Base Camp**. It drives
every Mountain device (Everest Max / Core, Everest 60, Makalu 67 / Max,
MacroPad, DisplayPad) with its own reverse‑engineered USB engines instead of
the fragile official SDK.

This page covers **how to install K2** and **how the Base Camp‑related options
work**. For the full feature list see the [README](../README.md).

---

## 1. Requirements

| | |
|---|---|
| OS | Windows 10 (1903+) or Windows 11, 64‑bit |
| Privileges | **Administrator** — K2 needs it to stop/start `BaseCampService` and to talk to some devices |
| Base Camp | **Not required to be running**, but its install folder (or just three DLLs from it) is needed for MacroPad, Everest Max and Everest 60 — see [§4.2](#42-base-camp-dll-folder) |
| Disk | ~600 MB installed (self‑contained .NET runtime, no separate install) |

MacroPad, Everest Max and Everest 60 rely on native DLLs that are internal
Base Camp components and are **not redistributed** with K2:
`MacroPadSDK.dll`, `SDKDLL.dll`, `Everest360_USB.dll`. DisplayPad and Makalu
work without them.

---

## 2. Getting K2

Download the latest build from the
[Releases page](https://github.com/FrankieDedo/K2/releases):

- **`K2-Setup-<version>.exe`** — Windows installer (recommended).
- **`K2-<version>.zip`** — portable package. Unzip anywhere and run
  `K2.App.exe`. All settings live in `%LocalAppData%\K2`, so the portable
  build is fully self‑contained except for that folder.

### Building it yourself

Clone the repo and run `build-check.bat` (builds both solutions). For the
installer, run `Installer\build-installer.bat`.

---

## 3. Installing with the setup wizard

1. **Run the installer as administrator.** It targets
   `C:\Program Files\K2` by default.
2. **Language** — the wizard first asks for its own language; K2's UI
   language can be changed later in Settings.
3. **Base Camp Components page** — tell K2 where the three native DLLs are:
   - *Use the Base Camp installation found on this PC* — pick this if the
     wizard detected one.
   - *Specify a folder manually* — browse to any folder that contains
     `MacroPadSDK.dll`, `SDKDLL.dll`, `Everest360_USB.dll` (e.g. a Base Camp
     install on another drive, or a folder where you copied just those
     files). The wizard warns if none are found but lets you continue.
   - Both choices can be changed later from **Settings → Base Camp DLL
     folder**.
4. **Base Camp Startup Behavior page** — two checkboxes, both changeable
   later in Settings:
   - *Close Base Camp automatically when K2 starts*
   - *Restart Base Camp automatically when K2 closes*
5. Finish. Launch K2 (it will request elevation).

---

## 4. First launch

- K2 asks for **administrator rights** every start. Without them it cannot
  stop `BaseCampService` and some devices stay unavailable.
- The **first time only**, if a Base Camp database is found, K2 offers to
  **import your existing profiles and settings** (see [§5.1](#51-base-camp-import)).
  You can re‑run this any time from Settings.
- If you never had Base Camp installed, no prompt appears.

---

## 5. Base Camp options (Settings tab)

All of the following live on the **Settings** tab, grouped by box.

### 5.1 Base Camp import

| Control | What it does |
|---|---|
| **Import from Base Camp…** | Looks for an existing Base Camp installation and imports its profiles and settings for **every** K2 device (Everest Max, Everest 60, Makalu, MacroPad, DisplayPad) plus the macro library. Each device shows its own confirmation/summary. Devices with no matching profile, or not connected, are simply skipped. |

Notes:

- Runs **automatically on first launch** if Base Camp data is found; this
  button forces it again whenever you want.
- Macros are imported **first**, so named‑macro key bindings resolve
  correctly.
- You can also import a single **Base Camp XML profile export** per device
  from that device's own page (Import button).
- *Restore all defaults* (bottom of Settings) wipes K2's stores and makes the
  first‑run import prompt appear again on the next start.

### 5.2 Base Camp DLL folder

Points K2 at the native DLLs it does not ship (`MacroPadSDK.dll`,
`SDKDLL.dll`, `Everest360_USB.dll`).

| Control | What it does |
|---|---|
| **Use the Base Camp installation found on this PC** | Auto‑detects a Base Camp install and loads the DLLs from it. Disabled if nothing is detected. |
| **Specify a folder manually** | Unlocks the folder picker below. Point it at any folder holding the three DLLs — no need to install Base Camp itself or set an environment variable. |
| Status line | Shows `found` / `missing` for each of the three DLLs with the current setting. |

DisplayPad and Makalu do not need this — they talk raw HID.

### 5.3 Base Camp interactions

| Checkbox | Default | Effect |
|---|---|---|
| **Automatically stop Base Camp on startup** | on | Every K2 start, stops `BaseCampService` and closes every Base Camp process (GUI, service, workers, Makalu monitor). K2 fully replaces Base Camp. Turn off if you want to keep using Base Camp alongside K2. |
| **Terminate Base Camp DisplayPad worker** | on | `MountainDisplayPadWorker` writes to the DisplayPad at the same time as K2 and corrupts icons. When enabled, K2 kills it and keeps it closed while running. |
| **Restart Base Camp when K2 closes** | off | On exit, relaunches whatever Base Camp processes K2 auto‑stopped, restoring the pre‑K2 state. The DisplayPad worker is always relaunched silently. Leave off if you want Base Camp to stay stopped. |
| **Start Base Camp services with Windows** | reflects system | Enables/disables Base Camp's Windows autostart entries (same as Task Manager → Startup, reversible). HKLM entries may need K2 run as administrator. |

If K2 stopped Base Camp and you want it back without restarting K2: reboot,
or start Base Camp manually.

### 5.4 SignalRGB coexistence

SignalRGB's bundled Mountain plugins use the **same USB interfaces** K2 uses
for LEDs. With both running, the LEDs flicker. This box decides who owns the
lighting — everything non‑lighting in K2 keeps working regardless.

| Mode | Effect |
|---|---|
| **Let SignalRGB drive the lighting** *(recommended)* | While SignalRGB runs, K2 stops writing effects / per‑key colors / brightness. When SignalRGB closes, K2 reapplies its own lighting. |
| **Close SignalRGB on K2 startup** | Every K2 start closes the SignalRGB engine and launcher (same as it does for Base Camp). The SignalRGB Windows service is left alone. |
| **Ignore SignalRGB** | K2 always drives the lighting. Only pick this if you never run SignalRGB on Mountain gear. |

**Plugins:**

| Button | What it does |
|---|---|
| **Install K2 plugins** | Copies K2's own Mountain device plugins into `Documents\WhirlwindFX\Plugins`, overriding the bundled ones with layouts/LED maps from K2's reverse engineering. Restart SignalRGB afterwards. |
| **Remove K2 plugins** | Removes them again. |
| **Open plugin folder** | Opens that folder in Explorer. |

---

## 6. Other relevant settings

| Setting | Notes |
|---|---|
| **Start K2 with Windows** | Adds a logon autostart entry. Because K2 needs admin and Windows does not auto‑elevate Run‑key entries, this may silently fail — if so, create a Task Scheduler task set to *Run with highest privileges* at logon. |
| **Language** | Restarts the app to apply. Saved to `%LocalAppData%\K2\K2.lang`. |
| **Accent color** | K2 Red / Mountain Blue, applied immediately (UI only, not device LEDs). |
| **Persist logs** | Keeps every session's log under `logs\` with a timestamp instead of overwriting. Useful for catching first‑launch crashes after an update. |

Runtime log (for bug reports): `%LocalAppData%\K2\K2.App\K2.App.log`.

---

## 7. Going back to Base Camp

1. In K2 Settings, turn **off** *Automatically stop Base Camp on startup* and
   *Terminate Base Camp DisplayPad worker*.
2. Turn **on** *Start Base Camp services with Windows* if you had disabled it.
3. Close K2. If Base Camp doesn't come back, reboot or start it manually.
4. Uninstalling K2 (Windows *Apps & features*, or delete the portable
   folder) leaves Base Camp untouched. K2's own data stays in
   `%LocalAppData%\K2` — delete it manually for a clean wipe.

---

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| A device page says the SDK/DLL is missing | Settings → **Base Camp DLL folder**: check the status line, point K2 at a valid folder. |
| DisplayPad icons flicker or look corrupted | Enable **Terminate Base Camp DisplayPad worker**. |
| LEDs flicker / effects fight each other | Set the **SignalRGB** mode to *Let SignalRGB drive the lighting* (or close SignalRGB). |
| Base Camp keeps coming back | It's on Windows autostart — untick **Start Base Camp services with Windows**, or disable it in Task Manager → Startup. |
| "Import from Base Camp" finds nothing | Base Camp not installed / no database, or profiles are for devices not currently connected. Try a per‑device **XML import** instead. |
| K2 won't control `BaseCampService` | Run K2 as administrator. |
| Occasional crash on first launch after update | Enable **Persist logs**, reproduce, attach the timestamped log from `%LocalAppData%\K2\logs`. |
