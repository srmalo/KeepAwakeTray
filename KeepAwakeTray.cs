using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// KeepAwakeTray v2: system-tray icon that keeps the Win11 session awake by
// injecting a (0,0) mouse move every ~50s (resets the inactivity timer WITHOUT
// moving the cursor and WITHOUT keystrokes -- unlike Caffeine's F15 '~' leak).
//
// Mechanism notes:
//  - The mouse "nudge" defeats input-idle locks (secure screensaver, Machine
//    inactivity limit, Dynamic Lock). It does NOT defeat presence-sensor
//    "lock on leave".
//  - SetThreadExecutionState(SYSTEM|DISPLAY) is added to robustly block sleep /
//    display-off while pulsing. It does NOT prevent the lock screen, so it is
//    complementary to the nudge.
//
// v2 fixes over v1 (high + medium issues):
//  - MakeIcon no longer leaks the HICON/Bitmap (DestroyIcon + using + Clone).
//  - DPI aware: crisp tray icon + Schedule dialog on scaled laptops.
//  - start == end is now treated as "all day (24h)" instead of "never".
//  - SendInput uses SetLastError + return-value check + MOVE_NOCOALESCE.
//  - SetThreadExecutionState keeps sleep/display-off blocked while pulsing.
//  - Single-instance guard (per-session Mutex).
//  - Config file location is configurable:  -config <dir|file.ini>  (default ./).
//  - WeekdaysOnly hand-edit parsing no longer silently flips to false.
//  - tray / timer / icons / menu are disposed deterministically.
//
// Usage:  KeepAwakeTray.exe [-keepon] [-config <dir|file.ini>]
//   -keepon          start in "Keep Active" (schedule bypass), not persisted.
//   -config <path>   ini location; a directory or a *.ini file; env vars allowed
//                    (e.g. -config "%APPDATA%\KeepAwakeTray"). Default: next to exe.
//
// Build (.NET Framework, no project needed):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe ^
//     /out:KeepAwakeTray.exe ^
//     /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll ^
//     KeepAwakeTray.cs

namespace KeepAwakeTray {
    static class Program {
        static System.Threading.Mutex mutex;   // kept alive for process lifetime

        [STAThread]
        static void Main(string[] args) {
            bool createdNew;
            mutex = new System.Threading.Mutex(true, @"Local\KeepAwakeTray.SingleInstance", out createdNew);
            if (!createdNew) return;   // another instance already runs in this session

            TrySetDpiAware();          // must precede any window/GDI to take effect
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool keepOn = Array.IndexOf(args, "-keepon") >= 0;
            string iniPath = ResolveConfigPath(args);

            Application.Run(new TrayApp(keepOn, iniPath));
            GC.KeepAlive(mutex);
        }

        // ---- config path resolution -------------------------------------------
        // Default: KeepAwakeTray.ini next to the exe ("./").
        // Override:  -config <dir|file.ini>   (env vars expanded; relative -> exe dir)
        static string ResolveConfigPath(string[] args) {
            string baseDir = Application.StartupPath;
            string custom = GetArgValue(args, "-config");
            if (string.IsNullOrEmpty(custom))
                return Path.Combine(baseDir, "KeepAwakeTray.ini");

            string p = Environment.ExpandEnvironmentVariables(custom.Trim().Trim('"'));
            if (!Path.IsPathRooted(p)) p = Path.Combine(baseDir, p);
            if (p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return p;   // explicit file
            return Path.Combine(p, "KeepAwakeTray.ini");                            // directory
        }

        static string GetArgValue(string[] args, string key) {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        // ---- DPI awareness (best effort, newest API first) --------------------
        static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value); // 2 = per-monitor
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static void TrySetDpiAware() {
            try { if (SetProcessDpiAwarenessContext(DPI_PER_MONITOR_AWARE_V2)) return; } catch { }
            try { SetProcessDpiAwareness(2); return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }
    }

    public class TrayApp : ApplicationContext {
        // ---- Win32 interop ----------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT {
            public int dx; public int dy; public uint mouseData;
            public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);
        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        const uint INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;
        const uint ES_DISPLAY_REQUIRED = 0x00000002;
        const int  IntervalMs = 50000;   // 50s, comfortably below a 60s+ lock policy

        static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

        // ---- UI / state -------------------------------------------------------
        NotifyIcon tray;
        ContextMenu menu;
        Timer timer;
        Icon iconOn, iconWait, iconOff;
        MenuItem miToggle, miKeep;

        readonly string iniPath;
        bool active = true;
        bool bypassSchedule = false;          // "Keep Active": session override, not persisted
        TimeSpan startT = new TimeSpan(8, 0, 0);
        TimeSpan endT   = new TimeSpan(19, 0, 0);
        bool weekdaysOnly = true;

        bool keepAwakeAsserted = false;       // tracks SetThreadExecutionState state
        bool disposed = false;

        public TrayApp(bool keepOn, string configPath) {
            iniPath = configPath;
            LoadConfig();
            if (keepOn) { bypassSchedule = true; active = true; }

            int sz = Math.Max(16, SystemInformation.SmallIconSize.Width);
            iconOn   = MakeIcon(Color.FromArgb(40, 180, 70), sz);    // green  = pulsing
            iconWait = MakeIcon(Color.FromArgb(225, 185, 40), sz);   // yellow = waiting
            iconOff  = MakeIcon(Color.FromArgb(130, 130, 130), sz);  // gray   = paused

            miToggle = new MenuItem("Active", OnToggle); miToggle.Checked = active;
            miKeep   = new MenuItem("Keep Active (ignore schedule)", OnKeepActive); miKeep.Checked = bypassSchedule;
            MenuItem miConfig = new MenuItem("Schedule...", OnConfig);
            MenuItem miExit   = new MenuItem("Exit", OnExit);
            // NOTE: ContextMenu/MenuItem are .NET Framework only. To port to .NET 5+,
            // swap to ContextMenuStrip/ToolStripMenuItem/ToolStripSeparator.
            menu = new ContextMenu(new MenuItem[] { miToggle, miKeep, miConfig, new MenuItem("-"), miExit });

            tray = new NotifyIcon();
            tray.ContextMenu = menu;
            tray.Visible = true;
            tray.DoubleClick += OnToggle;

            timer = new Timer();
            timer.Interval = IntervalMs;
            timer.Tick += OnTick;
            timer.Start();

            Reevaluate();   // initial pulse / keep-awake / icon
        }

        bool InWindow() {
            DateTime now = DateTime.Now;
            if (weekdaysOnly && (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday))
                return false;
            if (startT == endT) return true;              // equal start/end = all day (24h)
            TimeSpan t = now.TimeOfDay;
            if (startT < endT) return t >= startT && t < endT;   // normal window
            return t >= startT || t < endT;                      // window crossing midnight
        }

        bool ShouldPulse() { return active && (bypassSchedule || InWindow()); }

        // Single place that applies current state: pulse, keep-awake hint, icon.
        void Reevaluate() {
            bool pulse = ShouldPulse();
            ApplyKeepAwake(pulse);
            if (pulse) Nudge();
            UpdateIcon();
        }

        void OnTick(object sender, EventArgs e) { Reevaluate(); }

        void OnToggle(object sender, EventArgs e) {
            active = !active;
            if (!active) { bypassSchedule = false; miKeep.Checked = false; }  // pausar cancela el override
            miToggle.Checked = active;
            Reevaluate();
        }

        void OnKeepActive(object sender, EventArgs e) {
            bypassSchedule = !bypassSchedule;
            if (bypassSchedule) { active = true; miToggle.Checked = true; }    // "Keep Active" implica activar
            miKeep.Checked = bypassSchedule;
            Reevaluate();
        }

        void UpdateIcon() {
            string win = (startT == endT) ? "24h"
                : string.Format("{0:00}:{1:00}-{2:00}:{3:00}", startT.Hours, startT.Minutes, endT.Hours, endT.Minutes);
            if (!active) {
                tray.Icon = iconOff;  tray.Text = "KeepAwake: paused";
            } else if (bypassSchedule) {
                tray.Icon = iconOn;   tray.Text = "KeepAwake: active (Keep Active)";
            } else if (InWindow()) {
                tray.Icon = iconOn;   tray.Text = "KeepAwake: active (" + win + ")";
            } else {
                tray.Icon = iconWait; tray.Text = "KeepAwake: waiting, outside " + win;
            }
        }

        void OnConfig(object sender, EventArgs e) {
            float s = DpiScale();
            Func<int, int> P = v => (int)Math.Round(v * s);

            using (Form f = new Form()) {
                f.Text = "Schedule";
                f.Font = SystemFonts.MessageBoxFont;       // native look; scales by DPI (point-based)
                f.AutoScaleMode = AutoScaleMode.None;      // we scale manually -> no double scaling
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.Manual;   // open near the tray / cursor, not center-screen
                f.ClientSize = new Size(P(250), P(170));
                f.ShowInTaskbar = false;
                Point cur = Cursor.Position;                  // where the tray menu was just clicked
                Rectangle wa = Screen.FromPoint(cur).WorkingArea;
                int fx = Math.Min(Math.Max(wa.Left, cur.X - f.Width),  wa.Right  - f.Width);
                int fy = Math.Min(Math.Max(wa.Top,  cur.Y - f.Height), wa.Bottom - f.Height);
                f.Location = new Point(fx, fy);

                Label l1 = new Label(); l1.Text = "Start time:"; l1.SetBounds(P(20), P(24), P(90), P(20));
                Label l2 = new Label(); l2.Text = "End time:";   l2.SetBounds(P(20), P(58), P(90), P(20));

                DateTimePicker dtpStart = NewTimePicker(startT); dtpStart.SetBounds(P(120), P(20), P(100), P(24));
                DateTimePicker dtpEnd   = NewTimePicker(endT);   dtpEnd.SetBounds(P(120), P(54), P(100), P(24));

                CheckBox chk = new CheckBox();
                chk.Text = "Weekdays only (Mon-Fri)"; chk.Checked = weekdaysOnly;
                chk.SetBounds(P(20), P(92), P(210), P(22));

                Button ok = new Button(); ok.Text = "Save"; ok.SetBounds(P(50), P(128), P(80), P(28));
                ok.DialogResult = DialogResult.OK;
                Button cancel = new Button(); cancel.Text = "Cancel"; cancel.SetBounds(P(140), P(128), P(80), P(28));
                cancel.DialogResult = DialogResult.Cancel;

                f.Controls.AddRange(new Control[] { l1, l2, dtpStart, dtpEnd, chk, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;

                if (f.ShowDialog() == DialogResult.OK) {
                    startT = new TimeSpan(dtpStart.Value.Hour, dtpStart.Value.Minute, 0);
                    endT   = new TimeSpan(dtpEnd.Value.Hour, dtpEnd.Value.Minute, 0);
                    weekdaysOnly = chk.Checked;
                    SaveConfig();
                    Reevaluate();
                }
            }   // Form (and its child controls / pickers) disposed here
        }

        static DateTimePicker NewTimePicker(TimeSpan t) {
            DateTimePicker d = new DateTimePicker();
            d.Format = DateTimePickerFormat.Custom;
            d.CustomFormat = "HH:mm";
            d.ShowUpDown = true;
            d.Value = DateTime.Today.Add(t);
            return d;
        }

        void OnExit(object sender, EventArgs e) {
            if (tray != null) tray.Visible = false;   // hide immediately
            ExitThread();                              // ends loop; Run() disposes this context
        }

        static void Nudge() {
            INPUT[] inp = new INPUT[1];
            inp[0].type = INPUT_MOUSE;
            inp[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_MOVE_NOCOALESCE; // (0,0) move, not coalesced
            uint sent = SendInput(1, inp, InputSize);
            if (sent != 1)
                Debug.WriteLine("KeepAwake: SendInput inserted " + sent + " (err=" + Marshal.GetLastWin32Error() + ")");
        }

        void ApplyKeepAwake(bool on) {
            if (on == keepAwakeAsserted) return;        // only on transitions
            SetThreadExecutionState(on
                ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)   // block sleep + display-off
                : ES_CONTINUOUS);                                             // release
            keepAwakeAsserted = on;
        }

        // ---- config I/O -------------------------------------------------------
        void LoadConfig() {
            try {
                if (!File.Exists(iniPath)) { SaveConfig(); return; }   // create default
                foreach (string raw in File.ReadAllLines(iniPath)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (key == "starttime") startT = ParseTime(val, startT);
                    else if (key == "endtime") endT = ParseTime(val, endT);
                    else if (key == "weekdaysonly") {
                        bool? b = ParseBool(val);
                        if (b.HasValue) weekdaysOnly = b.Value;   // unknown token -> keep current
                    }
                }
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: LoadConfig failed: " + ex.Message);   // fall back to defaults
            }
        }

        void SaveConfig() {
            try {
                string dir = Path.GetDirectoryName(iniPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(iniPath, new string[] {
                    "# KeepAwakeTray configuration (edit by hand or from the tray icon menu)",
                    "# File location is set with the -config <dir|file.ini> option (default: next to the exe).",
                    "StartTime="    + string.Format("{0:00}:{1:00}", startT.Hours, startT.Minutes),
                    "EndTime="      + string.Format("{0:00}:{1:00}", endT.Hours, endT.Minutes),
                    "WeekdaysOnly=" + (weekdaysOnly ? "1" : "0")
                });
            } catch (Exception ex) {
                Debug.WriteLine("KeepAwake: SaveConfig failed: " + ex.Message);
                if (tray != null)
                    tray.ShowBalloonTip(5000, "KeepAwake",
                        "Could not save settings to:\n" + iniPath + "\n(" + ex.Message + ")",
                        ToolTipIcon.Warning);
            }
        }

        static TimeSpan ParseTime(string s, TimeSpan fallback) {
            DateTime dt;
            if (DateTime.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return new TimeSpan(dt.Hour, dt.Minute, 0);
            TimeSpan ts;
            if (TimeSpan.TryParse(s, out ts)) return new TimeSpan(ts.Hours, ts.Minutes, 0);
            return fallback;
        }

        static bool? ParseBool(string s) {
            switch (s.Trim().ToLowerInvariant()) {
                case "1": case "true": case "yes": case "on": case "y": return true;
                case "0": case "false": case "no": case "off": case "n": return false;
                default: return null;   // unrecognized -> caller keeps current value
            }
        }

        // ---- DPI / icon rendering --------------------------------------------
        static float DpiScale() {
            try { uint d = GetDpiForSystem(); if (d > 0) return d / 96f; } catch { }
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; } catch { }
            return 1f;
        }

        static Icon MakeIcon(Color c, int size) {
            using (Bitmap bmp = new Bitmap(size, size)) {
                using (Graphics g = Graphics.FromImage(bmp)) {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    int inset = Math.Max(1, size / 8);
                    int d = size - 2 * inset;
                    using (Brush b = new SolidBrush(c)) g.FillEllipse(b, inset, inset, d, d);
                    using (Pen p = new Pen(Color.White, Math.Max(1f, size / 16f))) g.DrawEllipse(p, inset, inset, d, d);
                }
                IntPtr h = bmp.GetHicon();
                try {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();   // Clone owns its handle -> safe to Dispose later
                } finally {
                    DestroyIcon(h);                 // release the HICON returned by GetHicon()
                }
            }
        }

        // ---- cleanup ----------------------------------------------------------
        protected override void Dispose(bool disposing) {
            if (!disposed && disposing) {
                disposed = true;
                ApplyKeepAwake(false);              // release SetThreadExecutionState
                if (timer != null) { timer.Stop(); timer.Dispose(); }
                if (tray  != null) { tray.Visible = false; tray.Dispose(); }
                if (menu  != null) menu.Dispose();  // disposes its MenuItems too
                if (iconOn   != null) iconOn.Dispose();
                if (iconWait != null) iconWait.Dispose();
                if (iconOff  != null) iconOff.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
