# Changelog

All notable changes to KeepAwakeTray are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/).

## [2.0.0] — 2026-06-22

A robustness and correctness pass. The core mechanism (a `(0,0)` mouse nudge via
`SendInput`) is unchanged; everything around it was hardened.

### Added
- **Sleep / display-off prevention** while pulsing, via
  `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`.
  The hint is released when pulsing stops (paused / outside the window) and on exit.
  This is *complementary* to the nudge — it does not prevent the lock screen.
- **DPI awareness** (Per-Monitor-V2, with fallbacks). The tray icon is rendered at
  the system small-icon size and the Schedule dialog scales correctly on
  high-DPI / scaled laptops, instead of being bitmap-stretched (blurry).
- **Single-instance guard** — a per-session named mutex; launching a second
  instance exits immediately instead of stacking tray icons / timers.
- **Configurable config-file location** via `-config <dir|file.ini>` (environment
  variables expanded; relative paths resolved next to the exe). Default unchanged:
  `KeepAwakeTray.ini` next to the executable.
- `MOUSEEVENTF_MOVE_NOCOALESCE` on the nudge so the move event is not coalesced away.
- `SetCompatibleTextRenderingDefault(false)` and named constants for the magic numbers.

### Changed
- **`StartTime == EndTime` now means "all day (24h)"** (still subject to
  weekdays-only). Previously it produced an empty window that *never* pulsed — a
  silent foot-gun.
- **`WeekdaysOnly` parsing** accepts `1/true/yes/on/y` and `0/false/no/off/n`; an
  unrecognized hand-edited value now **keeps the current setting** instead of
  silently flipping to `false`.
- `SendInput` is now declared with `SetLastError = true` and its return value is
  checked (a dropped/blocked nudge is logged via `Debug`).
- CI build references `System.dll` explicitly (the new `System.Diagnostics.Debug`
  usage), alongside `System.Drawing.dll` and `System.Windows.Forms.dll`.
- The **Schedule dialog opens near the tray/cursor** (clamped on-screen) instead of
  centered on the screen.

### Fixed
- **GDI handle leak** in icon creation: the `HICON` from `Bitmap.GetHicon()` is now
  released with `DestroyIcon` (the icon is cloned into a handle-owning `Icon`), and
  the source `Bitmap` is disposed. Tray icon, timer, menu and icons are now disposed
  deterministically via a `Dispose` override.
- Failed config saves no longer fail completely silently — the target directory is
  created if missing, and a balloon tip surfaces a write failure.

### Notes
- Documented the **UIPI / elevation** behavior: a medium-integrity process's
  injected input is silently dropped while a higher-integrity (elevated) window is
  focused. The recommended fix is an **elevated scheduled task at logon** — see the
  README.

## [1.0.0]

Initial public version.

### Added
- System-tray keep-awake app using a `(0,0)` `SendInput` mouse move (no keystrokes,
  no cursor movement — unlike Caffeine's F15 `~` leak).
- 🟢 / 🟡 / ⚪ tray icon states; double-click toggle; right-click menu.
- Optional time window and weekdays-only gating; midnight-crossing windows.
- **Keep Active** session override and the `-keepon` startup flag.
- Human-editable `KeepAwakeTray.ini` next to the exe.
- GitHub Actions CI that builds with the .NET Framework `csc` and publishes the
  `KeepAwakeTray.exe` to a rolling `latest` release.
- Release-ready README with UI illustrations.
