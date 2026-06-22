# KeepAwakeTray

[![build](https://github.com/srmalo/KeepAwakeTray/actions/workflows/build.yml/badge.svg)](https://github.com/srmalo/KeepAwakeTray/actions/workflows/build.yml)

A tiny Windows system-tray app that keeps your session awake — **without typing
keystrokes and without moving the cursor**.

> **Download:** grab the latest compiled `KeepAwakeTray.exe` from the
> [**Releases**](https://github.com/srmalo/KeepAwakeTray/releases/latest) page
> (built automatically on every push), or build it yourself (see below).

Many corporate environments lock the workstation after a few minutes of
inactivity (a secure screen saver enforced by policy). Power-only tools (such as
PowerToys Awake) do **not** prevent this: they manage the sleep/display API but
not the user-inactivity counter. Tools like Caffeine reset the counter by
simulating the **F15** key — which works, but injects a stray `~` into terminal
apps like PuTTY.

KeepAwakeTray instead simulates a **mouse move of (0, 0)** via `SendInput`. That
resets the inactivity counter **without moving the cursor and without injecting
any character**, so terminals stay clean.

## Features

- 🟢 / 🟡 / ⚪ tray icon showing the current state.
- Manual on/off toggle (double-click or menu).
- Optional **time window** (e.g. 08:00–19:00) and **weekdays-only** gating.
- Settings dialog (right-click → *Schedule…*) with `HH:mm` time pickers.
- Human-editable config file next to the executable.
- Single self-contained `.exe`, no runtime dependencies beyond .NET Framework
  (already present on Windows 10/11).

## Interface

The tray icon shows the current state at a glance:

![Icon states](assets/icon-states.svg)

Right-click the icon for the menu, and open **Schedule…** to set the time window:

<p>
  <img src="assets/menu.svg" alt="Context menu" height="160">
  &nbsp;&nbsp;
  <img src="assets/schedule-dialog.svg" alt="Schedule dialog" height="210">
</p>

> Illustrations of the UI (labels and layout match the app).

## Usage

- **Double-click** the tray icon → toggle active/paused.
- **Right-click → Keep Active (ignore schedule)** → session override: keep awake
  regardless of the schedule (also forces Active). Not persisted — resets to the
  schedule when the process restarts.
- **Right-click → Schedule…** → set start/end time (`HH:mm`) and the
  weekdays-only option. Changes apply immediately and are saved.
- **Right-click → Exit** → quit.

It sends a pulse every 50 seconds, but **only** when **active** *and*
(**Keep Active** *or* inside the configured window) — and, if enabled, on
weekdays only.

The `-keepon` command-line flag starts the app with Keep Active already on
(handy for an "always on" shortcut).

> **Note on elevated apps:** if you run apps **as administrator** (e.g. an
> elevated terminal), run KeepAwakeTray elevated too. Windows (UIPI) blocks a
> normal-integrity process from sending input to a higher-integrity foreground
> window, so a non-elevated KeepAwakeTray won't keep the session awake while an
> elevated window is focused.

### Icon states

| Icon | Meaning |
|------|---------|
| 🟢 Green  | Active and pulsing (inside the window). |
| 🟡 Yellow | Active but waiting (outside the window — not pulsing). |
| ⚪ Gray   | Paused manually. |

## Configuration

`KeepAwakeTray.ini` is created next to the `.exe` on first run. Edit it from the
menu dialog or by hand:

```ini
StartTime=08:00
EndTime=19:00
WeekdaysOnly=1
```

Edits from the menu apply on the fly; edits by hand take effect on the next
start. Windows that cross midnight are supported (e.g. `20:00`–`06:00`).

## Build

No IDE required — the .NET Framework C# compiler ships with Windows:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe `
  /out:KeepAwakeTray.exe `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  KeepAwakeTray.cs
```

## Run at logon

Place a shortcut to `KeepAwakeTray.exe` in your Startup folder
(`shell:startup`). It starts **active**.

> Launch it from your **user session** (Startup shortcut, double-click, or
> `explorer.exe KeepAwakeTray.exe`). A process started by an automation host can
> be terminated when that host exits.

## Notes & limitations

- Prevents the **inactivity** lock only. It cannot stop a manual lock (Win+L) or
  a lid-close sleep.
- Once the screen is locked, it cannot unlock it — by design, simulated input
  from the default desktop does not reach the secure logon desktop.

## License

MIT
