using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClaudeStatusTray;

// One session's state file: %USERPROFILE%\.claude\statusbar\state.d\<id>.json
// Field names/casing match the hooks' output (update.js / lifecycle.js) exactly.
sealed class Session
{
    [JsonPropertyName("state")] public string State { get; set; } = "idle";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("started")] public bool Started { get; set; }
    [JsonPropertyName("startedAt")] public long StartedAt { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }

    // Not from the file — stamped on read from which provider's state.d it came.
    [JsonIgnore] public string Provider { get; set; } = "Claude";
}

// Pure rendering logic — no WinForms, no filesystem. Unit-testable (see --selftest).
static class Model
{
    // Higher wins. permission > tool > thinking > idle/done  (FR1).
    public static int Priority(string state) => state switch
    {
        "permission" => 3,
        "tool" => 2,
        "thinking" => 1,
        _ => 0, // idle, done, unknown
    };

    public sealed record View(string IconState, string Tooltip, List<string> Rows,
        string TopState, long TopStartedAt, string TopProvider);

    static string What(Session s) => string.IsNullOrEmpty(s.Label) ? s.State : s.Label;

    // "C"/"A" origin tag from the provider name (falls back to "?" if somehow empty).
    public static string Tag(string provider) => string.IsNullOrEmpty(provider) ? "?" : provider[..1].ToUpperInvariant();

    public static string RowLabel(Session s)
        => $"[{Tag(s.Provider)}] {(string.IsNullOrEmpty(s.Project) ? "(unknown)" : s.Project)} — {What(s)}";

    // "Xm Ys" elapsed since a turn started (FR3). Pure/testable.
    public static string Elapsed(long startedAt, long now)
    {
        long e = now - startedAt;
        if (e < 0) e = 0;
        return $"{e / 60}m {e % 60}s";
    }

    // Given the live sessions, pick the icon state, base tooltip, and the started-session rows (FR2).
    // The elapsed timer (time-dependent) is appended by the caller so this stays deterministic.
    public static View Evaluate(IReadOnlyList<Session> live)
    {
        // Highest priority wins across providers; ties break to the most recently active session.
        var top = live.OrderByDescending(s => Priority(s.State)).ThenByDescending(s => s.Ts).FirstOrDefault();
        string iconState = top?.State ?? "idle";

        string tooltip = top is null
            ? "Claude: idle"
            : (string.IsNullOrEmpty(top.Project) ? $"[{Tag(top.Provider)}] {What(top)}" : $"[{Tag(top.Provider)}] {What(top)} — {top.Project}");
        if (tooltip.Length > 63) tooltip = tooltip[..63]; // NotifyIcon.Text hard limit

        var rows = live
            .Where(s => s.Started)
            .OrderByDescending(s => Priority(s.State))
            .Select(RowLabel)
            .ToList();

        return new View(iconState, tooltip, rows, iconState, top?.StartedAt ?? 0, top?.Provider ?? "Claude");
    }
}

sealed class Settings
{
    public bool ShowTimer { get; set; } = true;              // FR6
    public bool CompletionSound { get; set; } = false;       // FR6: off by default
    public string IconColor { get; set; } = "Orange";        // FR6: "Orange" | "System"
    public bool AutoUpdateCheck { get; set; } = false;       // opt-in network; manual check always available
    public bool ShowPill { get; set; } = true;               // floating always-visible status label
    public int PillX { get; set; } = -1;                     // saved pill position (-1 = default bottom-right)
    public int PillY { get; set; } = -1;
    public bool Notifications { get; set; } = true;          // desktop balloon toasts per event
}

static class Program
{
    // Each provider publishes to its own state.d; a missing dir is just a provider that isn't running.
    static readonly (string Provider, string Dir)[] ProviderStateDirs =
    {
        ("Claude",      ProviderDir(".claude")),
        ("Antigravity", ProviderDir(".antigravity")),
    };

    static string ProviderDir(string home) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), home, "statusbar", "state.d");

    // Windows runs each hook through a transient cmd.exe, so the recorded ppid is almost always
    // dead by the time we poll — pid liveness alone would hide every session. File existence is the
    // true liveness signal (SessionEnd deletes the file on close); pid-alive and a recent ts are just
    // crash backstops. A session is dropped only when BOTH are gone. Window covers a person who steps
    // away from a permission prompt; a real crash orphan lingers this long, then rests.
    const long StaleSeconds = 4 * 3600;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();

        ApplicationConfiguration.Initialize();
        EnsureAutostart();
        using var app = new TrayApp();
        Application.Run();
        return 0;
    }

    // Small always-on-top, non-activating status label near the tray — the always-visible text the
    // notification area can't show inline. Draggable; hidden when no session is active.
    sealed class PillForm : Form
    {
        string _text = "";
        Color _dot = Color.Gray, _fore = Color.White;
        bool _drag; Point _dragStart;
        public Action<Point>? Moved;

        public PillForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Height = 26;
            Font = new Font("Segoe UI", 9f);
            MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _drag = true; _dragStart = e.Location; } };
            MouseMove += (_, e) => { if (_drag) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y); };
            MouseUp += (_, __) => { if (_drag) { _drag = false; Moved?.Invoke(Location); } };
        }

        // Show without stealing focus; keep out of alt-tab.
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/; return cp; }
        }

        public void SetStatus(string text, Color dot, bool light)
        {
            _text = text; _dot = dot;
            _fore = light ? Color.FromArgb(20, 20, 20) : Color.White;
            BackColor = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32);
            using (var g = CreateGraphics()) Width = (int)g.MeasureString(text, Font).Width + 34;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            int d = 10, w = Width - 1, h = Height - 1;
            path.AddArc(0, 0, d, d, 180, 90); path.AddArc(w - d, 0, d, d, 270, 90);
            path.AddArc(w - d, h - d, d, d, 0, 90); path.AddArc(0, h - d, d, d, 90, 90);
            path.CloseFigure();
            using (var bg = new SolidBrush(BackColor)) g.FillPath(bg, path);
            DrawSpark(g, 14f, Height / 2f, 2f, 6f, _dot, 8, 0, 1.6f);
            using (var tb = new SolidBrush(_fore)) g.DrawString(_text, Font, tb, 23, (Height - Font.Height) / 2f);
        }
    }

    // ---- WinForms shell ----
    sealed class TrayApp : IDisposable
    {
        readonly NotifyIcon _icon;
        readonly System.Windows.Forms.Timer _timer;
        readonly Dictionary<string, (DateTime mtime, Session data)> _cache = new();
        readonly Dictionary<string, Icon> _icons = new();
        readonly Dictionary<string, string> _lastState = new(); // sid -> last state, for done chime
        readonly Settings _cfg = LoadSettings();
        readonly PillForm _pill = new();
        string _lastSig = "";
        bool _lastLight;
        int _frame; // working-state animation (FR: low-fps, 4 frames)
        bool _promoted; // taskbar auto-promotion done for this run
        Action? _balloonClick; // one-shot action for the next balloon click

        public TrayApp()
        {
            _lastLight = LightTaskbar();
            _pill.Moved += loc => { _cfg.PillX = loc.X; _cfg.PillY = loc.Y; SaveSettings(_cfg); };
            _icon = new NotifyIcon { Visible = true, Text = "Claude: idle", Icon = IconFor("idle", "Claude") };
            _icon.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
            _icon.ContextMenuStrip = new ContextMenuStrip();
            _timer = new System.Windows.Forms.Timer { Interval = 400 }; // FR5
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
            Poll();
            if (_cfg.AutoUpdateCheck) _ = CheckForUpdates(_icon, silent: true);
        }

        void Poll()
        {
            if (!_promoted) _promoted = TryPromoteToTaskbar();

            var live = ReadLive();
            var view = Model.Evaluate(live);

            NotifyEvents(live); // per-event desktop notifications + sound

            // If the taskbar theme flipped, cached icons are the wrong contrast — drop them.
            bool light = LightTaskbar();
            if (light != _lastLight) { _lastLight = light; foreach (var i in _icons.Values) i.Dispose(); _icons.Clear(); _lastSig = ""; }

            // Tooltip carries the elapsed timer (FR3) — time-dependent, so set it every tick (cheap).
            string tooltip = view.Tooltip;
            if (_cfg.ShowTimer && (view.TopState == "thinking" || view.TopState == "tool") && view.TopStartedAt > 0)
            {
                string t = $" ({Model.Elapsed(view.TopStartedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds())})";
                tooltip = (view.Tooltip + t) is { Length: <= 63 } s ? s : view.Tooltip;
            }
            _icon.Text = tooltip;

            // Icon swaps every tick while working so the animation runs even when nothing else
            // changed (400ms/frame ≈ 2.5fps — deliberately low, per the animation-cost risk).
            bool working = view.IconState is "thinking" or "tool";
            _frame = working ? (_frame + 1) % AnimFrames : 0;
            _icon.Icon = IconFor(view.IconState, view.TopProvider, _frame);

            var started = live.Where(s => s.Started).OrderByDescending(s => Model.Priority(s.State)).ToList();
            UpdatePill(started.Count > 0 ? started[0] : null);

            // The menu is structural — rebuild only when the session set/states change (idle-cheap).
            string sig = view.IconState + "|" + string.Join(";", started.Select(s => s.SessionId + s.State));
            if (sig == _lastSig) return;
            _lastSig = sig;

            BuildMenu(started);
        }

        void BuildMenu(List<Session> started)
        {
            var menu = _icon.ContextMenuStrip!;
            menu.Items.Clear();
            if (started.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("No active sessions") { Enabled = false });
            else
                foreach (var s in started)
                {
                    int pid = s.Pid;
                    menu.Items.Add(new ToolStripMenuItem(Model.RowLabel(s), null, (_, _) => FocusSession(pid))); // FR4
                }

            menu.Items.Add(new ToolStripSeparator());

            // Settings (FR6) — checkable, persisted on toggle.
            var timer = new ToolStripMenuItem("Show timer", null, (_, _) => { _cfg.ShowTimer = !_cfg.ShowTimer; SaveSettings(_cfg); }) { Checked = _cfg.ShowTimer };
            var notify = new ToolStripMenuItem("Notifications", null, (_, _) => { _cfg.Notifications = !_cfg.Notifications; SaveSettings(_cfg); }) { Checked = _cfg.Notifications };
            var sound = new ToolStripMenuItem("Sound", null, (_, _) => { _cfg.CompletionSound = !_cfg.CompletionSound; SaveSettings(_cfg); }) { Checked = _cfg.CompletionSound };
            var color = new ToolStripMenuItem("Icon color");
            foreach (var mode in new[] { "Orange", "System" })
            {
                var m = mode;
                color.DropDownItems.Add(new ToolStripMenuItem(m, null, (_, _) =>
                {
                    _cfg.IconColor = m; SaveSettings(_cfg);
                    foreach (var i in _icons.Values) i.Dispose(); _icons.Clear(); _lastSig = ""; // re-tint
                }) { Checked = _cfg.IconColor == m });
            }
            var pill = new ToolStripMenuItem("Show status label", null, (_, _) => { _cfg.ShowPill = !_cfg.ShowPill; SaveSettings(_cfg); if (!_cfg.ShowPill) _pill.Hide(); }) { Checked = _cfg.ShowPill };
            var autoUpd = new ToolStripMenuItem("Check for updates at startup", null, (_, _) => { _cfg.AutoUpdateCheck = !_cfg.AutoUpdateCheck; SaveSettings(_cfg); }) { Checked = _cfg.AutoUpdateCheck };
            menu.Items.Add(pill);
            menu.Items.Add(timer);
            menu.Items.Add(notify);
            menu.Items.Add(sound);
            menu.Items.Add(color);
            menu.Items.Add(autoUpd);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Check for updates…", null, (_, _) => _ = CheckForUpdates(_icon, silent: false)));
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { _icon.Visible = false; Application.Exit(); }));
        }

        // Refresh (or hide) the floating status pill from the top session.
        void UpdatePill(Session? top)
        {
            if (!_cfg.ShowPill || top is null)
            {
                if (_pill.Visible) _pill.Hide();
                return;
            }
            string label = string.IsNullOrEmpty(top.Label) ? top.State : top.Label;
            string text = string.IsNullOrEmpty(top.Project) ? label : $"{label} · {top.Project}";
            if (_cfg.ShowTimer && top.State is "thinking" or "tool" && top.StartedAt > 0)
                text += " · " + Model.Elapsed(top.StartedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            text = $"[{Model.Tag(top.Provider)}] {text}";

            _pill.SetStatus(text, StateColor(top.State, top.Provider, _cfg.IconColor, _lastLight), _lastLight);
            // Dragged? honor the saved spot. Otherwise dock to the taskbar, re-anchored each tick so
            // the right edge stays pinned by the clock as the label width changes.
            _pill.Location = _cfg.PillX >= 0
                ? new Point(_cfg.PillX, _cfg.PillY)
                : TaskbarDockLocation(_pill.Width, _pill.Height);
            if (!_pill.Visible) _pill.Show();
        }

        // On each session's transition into permission/done, fire a distinct desktop notification
        // (balloon + sound). Suppressed on the first poll so a restart mid-session isn't a toast storm.
        void NotifyEvents(List<Session> live)
        {
            var ids = new HashSet<string>();
            foreach (var s in live)
            {
                ids.Add(s.SessionId);
                _lastState.TryGetValue(s.SessionId, out var prev);
                bool known = prev is not null;
                if (known && s.State != prev)
                {
                    string who = string.IsNullOrEmpty(s.Project) ? s.SessionId : s.Project;
                    string tagged = $"[{Model.Tag(s.Provider)}] {who}";
                    if (s.State == "permission")
                        Notify("Permission needed", $"{tagged} is awaiting your approval", ToolTipIcon.Warning, Chime.Kind.Permission, s.Pid);
                    else if (s.State == "done")
                        Notify("Turn complete", $"{tagged} finished", ToolTipIcon.Info, Chime.Kind.Completion, s.Pid);
                }
                _lastState[s.SessionId] = s.State;
            }
            foreach (var gone in _lastState.Keys.Where(k => !ids.Contains(k)).ToList()) _lastState.Remove(gone);
        }

        void Notify(string title, string text, ToolTipIcon icon, Chime.Kind sound, int pid)
        {
            if (_cfg.CompletionSound) Chime.Play(sound);
            if (!_cfg.Notifications) return;
            _balloonClick = () => FocusSession(pid); // click the toast -> focus that session
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = icon;
            _icon.ShowBalloonTip(5000);
        }

        // Windows 11 hides new tray icons in the overflow with no API to force-show them. But dragging
        // one to the taskbar just sets IsPromoted=1 under HKCU\Control Panel\NotifyIconSettings\<hash>,
        // keyed by ExecutablePath. We find our own entry and set it — retried until the entry exists,
        // and re-done every launch since Windows can mint a fresh entry per run. Best-effort.
        bool TryPromoteToTaskbar()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return true;
                if (Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return true; // dev host
                using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
                if (root is null) return true; // pre-Win11: no such key, nothing to do
                bool found = false, changed = false;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    if (k?.GetValue("ExecutablePath") is not string ep) continue;
                    if (!string.Equals(ep, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    if ((k.GetValue("IsPromoted") as int?) != 1)
                    {
                        k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        changed = true;
                    }
                }
                if (changed) { _icon.Visible = false; _icon.Visible = true; } // re-add so the shell re-reads promotion
                return found; // keep retrying next tick until Windows has registered our icon
            }
            catch { return true; } // give up quietly
        }

        Icon IconFor(string state, string provider, int frame = 0)
        {
            string key = $"{state}|{provider}|{frame}|{_cfg.IconColor}|{_lastLight}";
            if (_icons.TryGetValue(key, out var cached)) return cached;
            var ico = MakeIcon(state, provider, frame, _cfg.IconColor, _lastLight);
            _icons[key] = ico;
            return ico;
        }

        List<Session> ReadLive()
        {
            var live = new List<Session>();
            var seen = new HashSet<string>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var (provider, dir) in ProviderStateDirs)
            {
                if (!Directory.Exists(dir)) continue; // provider not running / never published
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                {
                    seen.Add(f);
                    Session? s;
                    try
                    {
                        var mtime = File.GetLastWriteTimeUtc(f);
                        if (_cache.TryGetValue(f, out var hit) && hit.mtime == mtime)
                            s = hit.data; // FR5: re-parse only changed files
                        else
                        {
                            s = JsonSerializer.Deserialize<Session>(File.ReadAllText(f));
                            if (s is not null) { s.Provider = provider; _cache[f] = (mtime, s); }
                        }
                    }
                    catch { continue; } // locked / partial write — skip this tick
                    if (s is null) continue;
                    if (!IsLive(Alive(s.Pid), s.Ts, now)) continue; // FR5
                    live.Add(s);
                }
            }
            foreach (var gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
                _cache.Remove(gone);
            return live;
        }

        public void Dispose()
        {
            _timer.Dispose();
            _icon.Dispose();
            _pill.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
    }

    const int AnimFrames = 4; // low frame count on purpose (tray-icon swap is costly on Windows)

    // Draw a 16px dot colored by state. permission gets an attention badge; working states (thinking/
    // tool) get a small satellite orbiting at `frame` of 4 positions — a cheap spinner. Working states
    // are always their brand color; the neutral idle dot follows the "Icon color" setting (FR6).
    // Claude = orange, Antigravity = blue (Gemini aesthetic). Working states carry the provider hue
    // so you can tell who's busy; permission/done stay universal (urgency/success read the same).
    static Color BrandColor(string provider) => provider == "Antigravity"
        ? Color.FromArgb(66, 133, 244)   // Google blue
        : Color.DarkOrange;              // Claude

    // Shared by the tray icon and the floating pill so both agree on the color.
    static Color StateColor(string state, string provider, string iconColor, bool lightTaskbar)
    {
        Color neutral = iconColor == "System"
            ? (lightTaskbar ? Color.FromArgb(90, 90, 90) : Color.FromArgb(220, 220, 220))
            : Color.SlateGray;
        return state switch
        {
            "permission" => Color.Gold,
            "thinking" or "tool" => BrandColor(provider),
            "done" => Color.MediumSeaGreen,
            _ => neutral, // idle / unknown
        };
    }

    // Claude-style spark: tapered blades radiating from a center gap. Rotating it animates a spin.
    // Stylized/programmatic — swap for an official asset by loading a .ico if you ship one.
    public static void DrawSpark(Graphics g, float cx, float cy, float rInner, float rOuter, Color c, int rays, double rotDeg, float thick)
    {
        using var pen = new Pen(c, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < rays; i++)
        {
            double a = (rotDeg + i * (360.0 / rays)) * Math.PI / 180.0;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            g.DrawLine(pen, cx + dx * rInner, cy + dy * rInner, cx + dx * rOuter, cy + dy * rOuter);
        }
    }

    static Icon MakeIcon(string state, string provider, int frame, string iconColor, bool lightTaskbar)
    {
        Color c = StateColor(state, provider, iconColor, lightTaskbar);
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // Spin only while working; 45°/AnimFrames per frame wraps seamlessly (8 rays => 45° spacing).
            double rot = state is "thinking" or "tool" ? frame * (45.0 / AnimFrames) : 0;
            DrawSpark(g, 8f, 8f, 2.3f, 7.3f, c, 8, rot, 1.8f);
            if (state == "permission")
            {
                using var badge = new SolidBrush(Color.OrangeRed);
                g.FillEllipse(badge, 10, 0, 6, 6); // urgency dot
            }
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); } // Clone -> owns its own copy
        finally { DestroyIcon(h); }
    }

    // Keep a session unless BOTH signals say gone: pid dead AND file stale. (Windows ppid is usually
    // a dead transient shell, so ts freshness carries liveness there.)
    public static bool IsLive(bool pidAlive, long ts, long now) => pidAlive || now - ts <= StaleSeconds;

    static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; } // ArgumentException => not running
    }

    static void EnsureAutostart() // FR8
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var name = Path.GetFileName(exe);
            // Skip when running under the SDK host (dev: `dotnet run` -> dotnet.exe), not the real app.
            if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            k?.SetValue("ClaudeStatusTray", $"\"{exe}\"");
        }
        catch { /* autostart is best-effort */ }
    }

    // ---- settings (FR6): %USERPROFILE%\.claude\statusbar\settings.json ----
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "statusbar", "settings.json");

    static Settings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { return new(); } // missing/corrupt => defaults
    }

    static void SaveSettings(Settings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    // Light taskbar? Registry SystemUsesLightTheme (0=dark, 1=light). Default dark on any error.
    static bool LightTaskbar()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    // ---- click-to-focus (FR4), best-effort ----
    // A CLI session's `claude` pid owns no window — the terminal (an ancestor) does. Walk up the
    // parent chain to the first process with a main window and bring it forward.
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, ref ProcessBasicInformation pbi, int len, out int ret);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? win);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? win);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    struct RECT { public int Left, Top, Right, Bottom; }

    // Place the pill on the primary taskbar, right-anchored just left of the notification area
    // (the reliably-empty strip next to the clock) and vertically centered in the taskbar. Falls
    // back to floating above the tray if the taskbar can't be located (autohide, unusual shell).
    public static Point TaskbarDockLocation(int width, int height)
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && notify != IntPtr.Zero && GetWindowRect(tray, out var tb) && GetWindowRect(notify, out var nt))
            {
                int y = tb.Top + ((tb.Bottom - tb.Top) - height) / 2;
                int x = nt.Left - width - 8;
                if (x > tb.Left) return new Point(x, y);
            }
        }
        catch { }
        var wa = Screen.PrimaryScreen!.WorkingArea;
        return new Point(wa.Right - width - 12, wa.Bottom - height - 12);
    }

    static int ParentPid(Process p)
    {
        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0) return 0;
            return pbi.InheritedFromUniqueProcessId.ToInt32();
        }
        catch { return 0; }
    }

    static void FocusSession(int pid)
    {
        try
        {
            for (int i = 0; i < 8 && pid > 0; i++)
            {
                Process p;
                try { p = Process.GetProcessById(pid); } catch { return; }
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                    return;
                }
                pid = ParentPid(p);
            }
        }
        catch { /* best-effort; some terminals expose no focusable window */ }
    }

    // ---- event chimes: synthesized WAVs, no bundled asset, no license question. Distinct per event. ----
    static class Chime
    {
        public enum Kind { Completion, Permission }

        // Completion: two rising notes (a satisfied "ding-dong"). Permission: a two-pulse higher beep
        // (an attention "beep-beep") so the ear can tell them apart without looking.
        static readonly byte[] Completion = Build(new[] { 880.0, 1174.66 });
        static readonly byte[] Permission = Build(new[] { 1046.5, 1046.5 });

        public static void Play(Kind kind)
        {
            try { using var ms = new MemoryStream(kind == Kind.Permission ? Permission : Completion); new System.Media.SoundPlayer(ms).Play(); }
            catch { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } } // fallback
        }

        // 16-bit mono 44.1kHz; each note gets a short fade so it doesn't click.
        static byte[] Build(double[] notes)
        {
            const int rate = 44100;
            int per = rate / 6; // ~0.16s per note
            int n = per * notes.Length;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            void Chunk(string id) => w.Write(System.Text.Encoding.ASCII.GetBytes(id));
            int dataBytes = n * 2;
            Chunk("RIFF"); w.Write(36 + dataBytes); Chunk("WAVE");
            Chunk("fmt "); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            Chunk("data"); w.Write(dataBytes);

            for (int note = 0; note < notes.Length; note++)
                for (int i = 0; i < per; i++)
                {
                    double t = i / (double)rate;
                    double env = Math.Min(1.0, i / (rate * 0.01)) * Math.Min(1.0, (per - i) / (rate * 0.04));
                    double v = Math.Sin(2 * Math.PI * notes[note] * t) * env * 0.3;
                    w.Write((short)(v * short.MaxValue));
                }
            w.Flush();
            return ms.ToArray();
        }
    }

    // ---- update check (optional; GitHub releases) ----
    // Set to the published repo once it exists; the check is a no-op / error balloon until then.
    const string Repo = "m1ckc3s/claude-status-bar";

    static async Task CheckForUpdates(NotifyIcon icon, bool silent)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeStatusTray");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = ParseVer(tag);
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

            if (latest is not null && latest > current)
            {
                string url = doc.RootElement.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";
                icon.BalloonTipTitle = "Update available";
                icon.BalloonTipText = $"{tag} is available (you have {current}). Click to open.";
                void Open(object? _, EventArgs __) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } icon.BalloonTipClicked -= Open; }
                icon.BalloonTipClicked += Open;
                icon.ShowBalloonTip(6000);
            }
            else if (!silent)
            {
                icon.BalloonTipTitle = "Up to date";
                icon.BalloonTipText = $"You're on the latest version ({current}).";
                icon.ShowBalloonTip(4000);
            }
        }
        catch when (silent) { /* startup check stays quiet on failure */ }
        catch
        {
            icon.BalloonTipTitle = "Update check failed";
            icon.BalloonTipText = "Couldn't reach GitHub.";
            icon.ShowBalloonTip(4000);
        }
    }

    static Version? ParseVer(string tag)
    {
        var t = tag.TrimStart('v', 'V');
        return Version.TryParse(t, out var v) ? v : null;
    }

    // ---- self-check (no framework): dotnet run -- --selftest ----
    static int SelfTest()
    {
        void Check(bool ok, string msg) { if (!ok) throw new Exception("FAIL: " + msg); }

        int self = Environment.ProcessId;
        int dead = 999_999_990; // implausible pid

        Check(Alive(self), "current process should be alive");
        Check(!Alive(dead), "bogus pid should be dead");
        Check(!Alive(0), "pid 0 should be dead");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Windows liveness: dead pid but fresh file stays; dead + stale drops; alive always stays.
        Check(IsLive(pidAlive: false, ts: now, now: now), "dead pid + fresh file => live (Windows path)");
        Check(!IsLive(pidAlive: false, ts: now - StaleSeconds - 1, now: now), "dead pid + stale file => dropped");
        Check(IsLive(pidAlive: true, ts: now - StaleSeconds - 1, now: now), "alive pid => live even if stale (macOS path)");
        var sessions = new List<Session>
        {
            new() { State = "thinking", Project = "alpha", Started = true, Pid = self, Ts = now, Label = "Thinking…", Provider = "Claude" },
            new() { State = "permission", Project = "beta", Started = true, Pid = self, Ts = now, Label = "Awaiting permission", Provider = "Claude" },
            new() { State = "idle", Project = "gamma", Started = false, Pid = self, Ts = now, Provider = "Claude" },
        };
        var v = Model.Evaluate(sessions);
        Check(v.IconState == "permission", "permission must win priority");
        Check(v.Tooltip.Contains("beta"), "tooltip should name the winning project");
        Check(v.Rows.Count == 2, "only started sessions listed (idle/unstarted excluded)");
        Check(v.Rows[0].Contains("beta"), "rows sorted by priority, permission first");

        // Multi-provider: aggregate across providers, cross-provider priority, and [C]/[A] tags.
        var multi = new List<Session>
        {
            new() { State = "thinking", Project = "current-cloud", Started = true, Pid = self, Ts = now, Provider = "Claude" },
            new() { State = "permission", Project = "notification-ide-ai", Started = true, Pid = self, Ts = now, Provider = "Antigravity" },
        };
        var mv = Model.Evaluate(multi);
        Check(mv.IconState == "permission" && mv.TopProvider == "Antigravity", "Antigravity permission overrides Claude thinking");
        Check(mv.Rows.Exists(r => r.StartsWith("[A]")) && mv.Rows.Exists(r => r.StartsWith("[C]")), "rows carry [A]/[C] origin tags");
        Check(mv.Tooltip.StartsWith("[A]"), "tooltip tags the winning provider");
        Check(Model.Tag("Antigravity") == "A" && Model.Tag("Claude") == "C", "provider tag is first letter, uppercased");

        Check(Model.Evaluate(new List<Session>()).IconState == "idle", "no sessions => idle");
        Check(Model.Evaluate(new List<Session>()).Tooltip == "Claude: idle", "empty tooltip");

        // FR3 elapsed timer formatting
        Check(Model.Elapsed(now - 75, now) == "1m 15s", "elapsed formats mm ss");
        Check(Model.Elapsed(now + 10, now) == "0m 0s", "negative elapsed clamps to zero");
        Check(v.TopStartedAt == 0, "permission winner has startedAt 0 (no timer)");

        // FR6 settings round-trip (defaults + persistence)
        var def = new Settings();
        Check(def.ShowTimer && !def.CompletionSound && def.IconColor == "Orange", "settings defaults");
        var json = JsonSerializer.Serialize(new Settings { ShowTimer = false, IconColor = "System" });
        var back = JsonSerializer.Deserialize<Settings>(json)!;
        Check(!back.ShowTimer && back.IconColor == "System", "settings round-trip through json");

        // M4: update version comparison + icon frames + chime WAV is well-formed
        Check(ParseVer("v1.2.3") == new Version(1, 2, 3), "tag 'v1.2.3' parses");
        Check(ParseVer("not-a-tag") is null, "garbage tag => null (no crash)");
        Check(new Version(1, 0, 1) > new Version(1, 0, 0), "newer version compares greater");
        Check(AnimFrames >= 2 && AnimFrames <= 4, "animation frame count kept low");
        using (var i = MakeIcon("thinking", "Antigravity", 2, "Orange", false)) Check(i.Width > 0, "animated icon frame renders");
        foreach (var fld in new[] { "Completion", "Permission" })
        {
            var wav = typeof(Chime).GetField(fld, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as byte[];
            Check(wav is not null && wav.Length > 44 && wav[0] == (byte)'R' && wav[8] == (byte)'W', $"{fld} chime is a non-empty RIFF/WAVE buffer");
        }

        Console.WriteLine("selftest OK");
        return 0;
    }
}
