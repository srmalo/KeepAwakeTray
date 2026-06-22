using System;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// KeepAwakeTray: a system-tray icon that keeps the session awake by simulating
// a mouse move of (0,0) -> it resets the inactivity counter WITHOUT moving the
// cursor and WITHOUT injecting keystrokes (unlike Caffeine, which used F15 and
// leaked a '~' into PuTTY terminals).
//
// - Double-click / "Active" menu: toggles the manual switch.
// - Pulses only when ACTIVE **and** inside the configured time window.
// - "Schedule..." menu: edit start/end (HH:mm) and weekdays-only.
// - Config persisted in KeepAwakeTray.ini, next to the exe.
// - Icon: green = pulsing | yellow = active but outside the window | gray = paused.

namespace KeepAwakeTray {
    static class Program {
        [STAThread]
        static void Main(string[] args) {
            Application.EnableVisualStyles();
            bool keepOn = Array.IndexOf(args, "-keepon") >= 0;
            Application.Run(new TrayApp(keepOn));
        }
    }

    public class TrayApp : ApplicationContext {
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT {
            public int dx; public int dy; public uint mouseData;
            public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }
        [DllImport("user32.dll")]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        NotifyIcon tray;
        Timer timer;
        Icon iconOn, iconWait, iconOff;
        MenuItem miToggle;
        MenuItem miKeep;

        // State
        bool active = true;
        bool bypassSchedule = false;   // "Keep Active": override de sesion, NO se persiste
        TimeSpan startT = new TimeSpan(8, 0, 0);
        TimeSpan endT   = new TimeSpan(19, 0, 0);
        bool weekdaysOnly = true;

        string IniPath { get { return Path.Combine(Application.StartupPath, "KeepAwakeTray.ini"); } }

        public TrayApp(bool keepOn) {
            LoadConfig();
            if (keepOn) { bypassSchedule = true; active = true; }   // -keepon: arranca con Keep Active (override de sesion, no se persiste)

            iconOn   = MakeIcon(Color.FromArgb(40, 180, 70));    // green  = pulsing
            iconWait = MakeIcon(Color.FromArgb(225, 185, 40));   // yellow = waiting
            iconOff  = MakeIcon(Color.FromArgb(130, 130, 130));  // gray   = paused

            miToggle = new MenuItem("Active", OnToggle);
            miToggle.Checked = active;
            miKeep = new MenuItem("Keep Active (ignore schedule)", OnKeepActive);
            miKeep.Checked = bypassSchedule;
            MenuItem miConfig = new MenuItem("Schedule...", OnConfig);
            MenuItem miExit = new MenuItem("Exit", OnExit);
            ContextMenu menu = new ContextMenu(new MenuItem[] {
                miToggle, miKeep, miConfig, new MenuItem("-"), miExit
            });

            tray = new NotifyIcon();
            tray.ContextMenu = menu;
            tray.Visible = true;
            tray.DoubleClick += OnToggle;

            timer = new Timer();
            timer.Interval = 50000;  // 50s, well below the 600s lock
            timer.Tick += OnTick;
            timer.Start();

            UpdateIcon();
            if (active && InWindow()) Nudge();  // initial pulse only if applicable
        }

        bool InWindow() {
            DateTime now = DateTime.Now;
            if (weekdaysOnly && (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday))
                return false;
            TimeSpan t = now.TimeOfDay;
            if (startT <= endT) return t >= startT && t < endT;     // normal window
            return t >= startT || t < endT;                          // window that crosses midnight
        }

        // Pulsa si esta activo y (override "Keep Active" o dentro de la ventana)
        bool ShouldPulse() { return active && (bypassSchedule || InWindow()); }

        void OnTick(object sender, EventArgs e) {
            if (ShouldPulse()) Nudge();
            UpdateIcon();  // refresh color in case the window boundary was crossed
        }

        void OnToggle(object sender, EventArgs e) {
            active = !active;
            if (!active) {                 // pausar manualmente cancela el override
                bypassSchedule = false;
                miKeep.Checked = false;
            }
            miToggle.Checked = active;
            UpdateIcon();
            if (ShouldPulse()) Nudge();
        }

        void OnKeepActive(object sender, EventArgs e) {
            bypassSchedule = !bypassSchedule;
            if (bypassSchedule) {          // "Keep Active" implica activar
                active = true;
                miToggle.Checked = true;
            }
            miKeep.Checked = bypassSchedule;
            UpdateIcon();
            if (ShouldPulse()) Nudge();
        }

        void UpdateIcon() {
            string win = string.Format("{0:00}:{1:00}-{2:00}:{3:00}",
                startT.Hours, startT.Minutes, endT.Hours, endT.Minutes);
            if (!active) {
                tray.Icon = iconOff;
                tray.Text = "KeepAwake: paused";
            } else if (bypassSchedule) {
                tray.Icon = iconOn;
                tray.Text = "KeepAwake: active (schedule bypassed - Keep Active)";
            } else if (InWindow()) {
                tray.Icon = iconOn;
                tray.Text = "KeepAwake: active (" + win + ")";
            } else {
                tray.Icon = iconWait;
                tray.Text = "KeepAwake: waiting, outside " + win;
            }
        }

        void OnConfig(object sender, EventArgs e) {
            Form f = new Form();
            f.Text = "Schedule";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new Size(250, 160);
            f.ShowInTaskbar = false;

            Label l1 = new Label(); l1.Text = "Start time:"; l1.SetBounds(20, 22, 90, 20);
            Label l2 = new Label(); l2.Text = "End time:";   l2.SetBounds(20, 56, 90, 20);

            DateTimePicker dtpStart = NewTimePicker(startT); dtpStart.SetBounds(120, 18, 100, 24);
            DateTimePicker dtpEnd   = NewTimePicker(endT);   dtpEnd.SetBounds(120, 52, 100, 24);

            CheckBox chk = new CheckBox();
            chk.Text = "Weekdays only (Mon-Fri)";
            chk.Checked = weekdaysOnly;
            chk.SetBounds(20, 88, 210, 22);

            Button ok = new Button(); ok.Text = "Save"; ok.SetBounds(50, 120, 80, 28);
            ok.DialogResult = DialogResult.OK;
            Button cancel = new Button(); cancel.Text = "Cancel"; cancel.SetBounds(140, 120, 80, 28);
            cancel.DialogResult = DialogResult.Cancel;

            f.Controls.AddRange(new Control[] { l1, l2, dtpStart, dtpEnd, chk, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;

            if (f.ShowDialog() == DialogResult.OK) {
                startT = new TimeSpan(dtpStart.Value.Hour, dtpStart.Value.Minute, 0);
                endT   = new TimeSpan(dtpEnd.Value.Hour, dtpEnd.Value.Minute, 0);
                weekdaysOnly = chk.Checked;
                SaveConfig();
                UpdateIcon();
                if (active && InWindow()) Nudge();
            }
            f.Dispose();
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
            tray.Visible = false;
            timer.Stop();
            Application.Exit();
        }

        static void Nudge() {
            INPUT[] inp = new INPUT[1];
            inp[0].type = 0;             // INPUT_MOUSE
            inp[0].mi.dwFlags = 0x0001;  // MOUSEEVENTF_MOVE with dx=dy=0
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        void LoadConfig() {
            try {
                if (File.Exists(IniPath)) {
                    foreach (string raw in File.ReadAllLines(IniPath)) {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();
                        if (key == "starttime") startT = ParseTime(val, startT);
                        else if (key == "endtime") endT = ParseTime(val, endT);
                        else if (key == "weekdaysonly") weekdaysOnly = (val == "1" || val.ToLowerInvariant() == "true");
                    }
                } else {
                    SaveConfig();  // create the ini with defaults
                }
            } catch { /* if the ini is corrupt, fall back to defaults */ }
        }

        static TimeSpan ParseTime(string s, TimeSpan fallback) {
            DateTime dt;
            if (DateTime.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return new TimeSpan(dt.Hour, dt.Minute, 0);
            TimeSpan ts;
            if (TimeSpan.TryParse(s, out ts)) return new TimeSpan(ts.Hours, ts.Minutes, 0);
            return fallback;
        }

        void SaveConfig() {
            try {
                string[] lines = new string[] {
                    "# KeepAwakeTray configuration (edit by hand or from the tray icon menu)",
                    "StartTime=" + string.Format("{0:00}:{1:00}", startT.Hours, startT.Minutes),
                    "EndTime="   + string.Format("{0:00}:{1:00}", endT.Hours, endT.Minutes),
                    "WeekdaysOnly=" + (weekdaysOnly ? "1" : "0")
                };
                File.WriteAllLines(IniPath, lines);
            } catch { }
        }

        static Icon MakeIcon(Color c) {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 12, 12);
                using (Pen p = new Pen(Color.White, 1)) g.DrawEllipse(p, 2, 2, 12, 12);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
